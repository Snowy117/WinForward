# Design — F2 warm-path lock chain: lock-free resolve, self-traffic reorder, UDP ready path

Scope: the four sites the PRD numbers — (1) the registry check on the warm entry, (2) `FlowTable`'s warm
resolve + activity touch, (3) the TCP redirect table's reverse-candidate + resolve pair, (4) the UDP
ready path's coordinator → session → cooldown chain. Nothing about routing, rewrite, injection, policy
evaluation or retention *envelopes* changes; what changes is which locks and clock reads a warm packet
pays, and the concrete representation of activity time.

Deliberately out of scope (PRD Notes): the §A4 sharded slab/index rebuild with a canonical key and
incremental rehash (roadmap item 7), the transport's send gate, the tombstone/cooldown/pending-SYN
representations (F3 contract item 5), and any change to the sweep's round shape (F3's `_liveStates`,
`_sweepGate`, minimal-granularity holds and `SweepHoldProbe` are load-bearing and stay).

Explicitly in scope because requirement 1 cannot be met without it: **the two flow-table indexes change
data structure, and the pooled `FlowState` is returned through a validated snapshot.** §2 states why the
existing `Dictionary` indexes cannot carry a lock-free read (the runtime's own torn-chain detection
throws `InvalidOperationException`, and on a pump thread that tears capture down), and why the pool
stays.

## 1. Chosen mechanism per site

| # | Site | Mechanism | Lock before → after | Probes before → after | Clock reads per packet before → after |
|---|---|---|---|---|---|
| 1 | dispatcher warm entry, self traffic (`FlowDispatcher.cs:162`) | **split the check**: the **exact-tuple** half runs only at claim time (`:194`), where a self tuple never produces a state; the **wildcard relay-socket** half stays on the warm entry as a lock-free `ConcurrentDictionary` probe (`IsWildcardOwned`) because dropping it lets a recycled ephemeral port redirect WinForward's own relay control connection (§4) | registry gate 1 → **0** | ≤4 → **≤2** on hits (the wildcard pair), ≤4 on the first packet of a flow | 0 → 0 |
| 2 | `FlowTable.TryResolve` (`FlowTable.cs:163`) | the gated `Dictionary` authority stays (a plain `Dictionary` read is unsafe, and a concurrent index costs 488 B per claim — §2.0); a **pre-allocated direct-mapped warm cache** of `FlowState` slots sits over it, validated by Step 2's seqlock snapshot + the canonical transport tuple | flow-table gate 1 → **0 on a cache hit**, unchanged on a miss | 1–2 → a hit is one array read + validation | 1 → **0** |
| 3 | `TcpRedirectTable` reverse pair (`TcpProxyCoordinator.cs:359` + `Injections.cs:305`) | the gated dictionaries stay the authority; **two pre-allocated direct-mapped caches** (reverse tuple, original key) over them, validated by the association's own immutable fields; `IsReverseCandidate` + `TryResolveByReverse` **fold into one** probe whose association is passed into `HandleReverseAsync`, so `Injections.cs:305` is deleted too; `IsReverseCandidatePort` stays the warm prefilter | redirect gate 2 → **0 on a cache hit** | 2 → 1 | 1 (`:386`, `:305`) → **0** (bucket-derived stamp) |
| 4 | UDP ready path (`UdpProxyCoordinator.Send.cs:21`) | **ready-first reorder**: the ready session is resolved off a **pre-allocated direct-mapped session cache**, validated by the session's own flow key; the cooldown probe and the clock read move to the cold (admission) path which keeps `_gate`; the session's send/touch paths drop `_activityGate` and the clock read | coordinator gate 1 + cooldown gate 1 + session gate 2 + transport semaphore 1 → **transport semaphore 1** on a cache hit | 2 → 1 | 2 → **0** |

The transport `_sendGate` is unchanged and named here so the after-column is not read as "one lock per
datagram in production": it serialises the shared send buffer and the relay rebind
(`Socks5UdpTransport.cs:306-359`, `:393-399`) and requirement 5 does not cover it.

## 2. `FlowTable`: a lock-free direct-mapped warm cache over the gate-guarded dictionary

### 2.0 The two rejected mechanisms (both measured, one of them landed and reverted)

1. **Lock-free reads over the plain `Dictionary`** — rejected with runtime-source evidence
   (`research/implementation-notes.md` §8.1): `Dictionary.Resize` publishes `_buckets` before `_entries`,
   `FindValue` reads them as two fields, and the runtime detects the resulting chain loop and throws
   `InvalidOperationException` ("a concurrent update has happened"); on a pump thread that tears capture
   down process-wide. The authority therefore stays a gated `Dictionary`.
2. **A concurrent index (`ConcurrentDictionary` for `_states`/`_transportIndex`)** — implemented on this
   tree, measured, and **reverted**. It delivered the thing it was chosen for: the warm arm's
   self-normalised four-thread ratio went 0.161/0.154/0.153 → **0.628**, the warm arm's four-thread rate
   3.29 M/s → **11.06 M/s**, and the two structural facts went red → green
   (`WarmResolveTakesNoFlowTableGateEntries` `Expected: 0, Actual: 256`; the parked-gate fact queued
   behind the gate). It cannot land because of the exact allocation gate:
   `HotPathAllocationGateTests.FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` measured
   `Expected: 0, Actual: 124,928` over 256 claim+expire cycles = **488 B per cycle**, factored as
   **3 nodes × ~160 B** (a bare `ConcurrentDictionary<FlowKey,int>` add+remove measures 160 B/cycle; the
   table inserts `_states`, `_transportIndex[tuple]` and `_transportIndex[tuple.Reverse()]`) plus ~8 B of
   incidental overhead. `ConcurrentDictionary` allocates a `Node` per insert and never reuses it, so the
   gate can never hold with it, and the gate protects the deliberate in-place `FlowState` pooling
   contract — the parent programme's PRD forbids regressing it, so the 488 B is **not** sanctioned and the
   gate is **not** re-baselined. Recorded as a measured dead end (notes §13, §10).

### 2.1 Shape: pre-allocated direct-mapped slots over the gated authority

| | today | after |
|---|---|---|
| `_states`, `_transportIndex` | `Dictionary`, mutated and read under `_gate` | **unchanged** (the authority) |
| warm read | `lock (_gate)` + 1–2 probes (+ clock) | **one volatile array read + validation**, no lock, no clock |
| warm miss | — | exactly today's locked path, then a write-through |
| slot storage | — | `private readonly FlowState?[] _warm`, allocated **once** in the ctor, never grown |
| `Count` / `Capacity` / enumeration | `_gate` | **unchanged** (no maintained counter is needed — this is the mechanism's biggest structural simplification over the rejected variant) |
| F3 sweep (`_liveStates`, chunked round, `SweepHoldProbe`) | — | **untouched** |

```csharp
// ctor — the only allocation this mechanism adds, ever
var slotTarget = (int)Math.Clamp((long)capacity * 64, 4_096, 262_144);
_warm = new FlowState?[BitOperations.RoundUpToPowerOf2((uint)slotTarget)];
```

| table capacity | slots | array | λ = live/slots at 4,096 live flows | modelled miss rate |
|---|---:|---:|---:|---:|
| 64 (unit tests) | 4,096 | 32 KB | — | — |
| 1,024 | 65,536 | 512 KB | 0.063 | ~6 % |
| 5,120 (the `scaling` scenario's table) | 262,144 | 2 MB | 0.016 | **~1.6 %** |
| 65,536 (shipped default, desktop 3–5k live) | 262,144 | 2 MB | 0.016 | **~1.6 %** |
| 65,536 with a full table (65,536 live) | 262,144 | 2 MB | 0.25 | ~22 % (degrades to the locked path, never wrong) |

**Memory, stated against the measurement**: +2 MB at the shipped default against the `flowTable` stage's
recorded 32.5 MB (`residency-census.jsonl`: 32,513,544 / 32,519,352 / 32,525,832 B over baseline), i.e.
**+6 %** — against a mechanism whose alternative (the reverted concurrent index) could not pass an exact
gate at all. The factor 64 and the 262,144 cap exist to hold λ ≈ 0.016 for the realistic working set
(§2.5); the cap is the single tunable if the measured ratio needs more headroom, and the memory delta is
recorded in the artifact.

### 2.2 The hash: order-independent, so forward and reverse share a slot

The cache must serve exactly what `TryResolveLocked` serves — `_states[key]` **or**
`_transportIndex[tuple]` in **either orientation** — so the slot must be selected by an
*order-independent* mix of the endpoint pair, while validation stays exact:

```csharp
// FlowHash gains one expression; the existing Combine stays the single mix.
internal static int CombineCanonical(AddressFamilyKind family, TransportProtocol protocol, Endpoint a, Endpoint b)
{
    // Order the two endpoints by (address bits, then port): a total order on endpoints, so a packet and
    // its reverse produce the same ordered pair and therefore the same slot.
    var (lo, hi) = (a.Address.Bits, a.Port).CompareTo((b.Address.Bits, b.Port)) <= 0 ? (a, b) : (b, a);
    return Combine(family, protocol, lo, hi);
}
```

`FlowHash.Combine` is already the one shared transport-only mix (`Domain.cs:129-138`) — reusing it keeps
"collisions are fine, divergence is not" intact and makes the cache's slot function the same function the
dictionary's bucket function uses. The hash costs one 128-bit compare and the existing mix (~5–10 ns), i.e.
a fraction of the lock it replaces.

### 2.3 The read path, exactly (validation, never approximation)

```csharp
/// Warm probe: no lock, no clock. False means "take the gated path" — never "the flow is absent".
public bool TryResolveWarm(FlowKey key, out FlowStateView view)
{
    var tuple = TransportTuple.From(key);
    var candidate = Volatile.Read(ref _warm[FlowHash.CombineCanonical(key.AddressFamily, key.Protocol, key.Local, key.Remote) & (_warm.Length - 1)]);
    if (candidate is not null && candidate.TrySnapshot(out view) && Matches(tuple, view.Key))
    {
        candidate.TouchBucket(_activityClock.Current);   // one volatile store, after validation
        return true;
    }
    view = default;
    return false;
}

private static bool Matches(in TransportTuple queried, FlowKey stored) =>
    TransportTuple.From(stored) == queried || TransportTuple.From(stored).Reverse() == queried;
```

- `candidate.TrySnapshot` is Step 2's landed barrier-bracketed seqlock (`Domain.cs:208-230`): acquire
  read → `Interlocked.MemoryBarrier()` → the three field reads → fence → the second acquire read. A torn
  read, an odd version, a changed version or a key mismatch is a **false miss**, never a false hit.
- `Matches` is the exact relation the two dictionary probes encode. It is *equivalent* to the OR of
  `_states[key]` and `_transportIndex[tuple]` because the table holds **at most one state per transport
  tuple**: `AddToTransportIndex` (`FlowTable.cs:393-398`) uses `Dictionary.Add`, which throws on a
  duplicate tuple, and `TryResolveLocked` (`:381-391`) resolves a second claim of the same tuple to the
  existing state instead of claiming (`TryClaimResolved`, `:178-203`). That invariant is load-bearing and
  gets its own regression fact (§9).
- The ABA cases are Step 2's, unchanged and accepted: a state recycled for **another** key fails
  `Matches` (false miss); a state removed and re-claimed for the **same** key passes and serves that
  key's current decision, with `Generation` possibly newer and log-only
  (`FlowDispatcher.cs:163-186` stamps it onto the packet; nothing routes on it). The `TouchBucket` write
  lands after validation and may store one bucket newer than the publication — monotone forward, harmless.

### 2.4 Population and invalidation: every site, named

| Event | Site (current tree) | Cache action |
|---|---|---|
| claim publishes a state | `TryClaimResolved` after `_states.Add` / `AddToTransportIndex` / the `_liveStates` append (`FlowTable.cs:178-203`) | `Volatile.Write(ref _warm[slot(key)], created)` under `_gate` |
| gated resolve hits (the miss path's write-through) | `TryResolveLocked` (`:381-391`) | `Volatile.Write(ref _warm[slot], state)` under `_gate` — so the **second** lookup of a collided flow is already warm |
| sweep removes a state | `RemoveCandidateAt` (`:327-350`), immediately **before** `ReturnState` (`:375-379`) | `if (ReferenceEquals(_warm[i], candidate)) Volatile.Write(ref _warm[i], null)` — never clear another flow's slot |

`RemoveExpired`/`RemoveCandidateAt` is the table's **only** removal path (`_states.Remove` appears once,
at `:335`; `ReturnState` once, at `:339`), so the invalidation set is complete by construction, and the
clear happens under the same `_gate` hold as the removal and **before** the state can return to
`_freeStates` — the F3 ordering rule extended to the cache. A reader that loaded the slot before the
clear holds a reference to a recycling state; the seqlock + `Matches` turn that into a false miss.

### 2.5 Miss rate, the ≥0.6 arithmetic, and the honest failure mode

**The failure mode is a false miss**: a colliding flow's slot is overwritten by another flow
(direct-mapped, overwrite on collision), so that flow's lookups fall back to exactly today's locked path
— never a wrong decision, never a wrong route (research §A5 forbids approximate structures for *flow
decisions*; this cache is exact, and its only lossy dimension is *which* flow is resident).

**Why the miss rate is not "just a few percent of free work" but a real driver.** With write-through, two
flows sharing a slot thrash: each lookup writes its own state into the slot, so the other's next lookup
misses. Under a round-robin walk every colliding flow misses essentially always, so the miss rate is well
modelled by the fraction of flows with at least one partner, `m ≈ 1 − e^(−λ)`. The sizing table above keeps
`m ≲ 2 %` for the realistic working set; the operator's illustrative `2 × capacity` sizing does **not** —
at the `scaling` scenario's capacity (5,120) it yields 10,240 slots, λ = 0.4 and `m ≈ 33 %`, which the
arithmetic below says fails.

**The arithmetic, anchored on the three measured points** (all on this host, warm arm, self-normalised
four-thread ratio):

| point | measured | note |
|---|---|---|
| fully locked (m = 1) | **0.155** (0.161 / 0.154 / 0.153) | recorded baseline, warm arm, 3 runs |
| fully lock-free (m = 0) | **0.628**, 11.06 M/s at 4 threads | the reverted concurrent-index patch |
| miss rate m | — | `m ≈ 1 − e^(−λ)`, §above |

Two models bracket the interpolation, and the design does not pretend to know which is true:

- *Linear-cost blend* (assumes the locked lookups pay the fully-contended ~1,481 ns even at small m):
  `ratio(m) = (227 + 73m) / (362 + 1119m)` — the 1-thread and 4-thread per-lookup costs measured above.
  It crosses 0.6 at **m ≈ 1.6 %**.
- *Gate-saturation model* (the gate serves 3.33 M ops/s; a 2 % miss rate is 0.22 M ops/s of demand, so
  the locked lookups stay essentially uncontended): `ratio(m) ≈ (227 + 73m) / (362 − 62m)`, which gives
  0.63 at m = 2 % and only collapses once the locked demand approaches the gate's service rate (m ≈ 0.3).

Both models agree that **m ≲ 2 % satisfies the criterion under either reading**, which is exactly what the
4-slot-per-λ sizing buys. They also expose the honest risk: the *ceiling itself is only 4.7 % above the
acceptance line* (0.628 vs 0.6), so the margin is thin by construction and the result must be measured,
not argued — which is why the measurement plan counts hits and misses in the scenario and records both
beside the ratio.

**If the measured ratio still lands below 0.6 at a measured m ≤ 2 %**, the residue is not the miss rate
and the honest next steps, in order, are: (i) raise the slot cap to 524,288 (4 MB, λ halves); (ii) K-way
probing (two or four candidate slots per lookup, which cuts the miss rate by an order of magnitude at the
same footprint, but changes the operator-specified "direct-mapped, overwrite on collision" shape and
therefore needs an explicit nod); (iii) if neither works, restate requirement 1 as "the warm resolve takes
no global lock for cache-resident flows, with the miss rate measured" — the PRD already authorises a
restatement over a silent weakening, and the restatement must quote the measured m and ratio.

### 2.6 What stays exactly as it is

`_gate`, `_sweepGate`, `_liveStates`/`_liveCount` and its append/swap-remove discipline, the
minimal-granularity round, `ScanChunk`/`RemoveCandidateAt`, `HoldProbe` and its counts, `_freeStates` and
`RentState`/`ReturnState` (exactly-once return, identity recycling), `Count`, `Capacity`, the fail-closed
capacity branch, and the two exact allocation gates. `TryClaimResolved` still runs `decide()` inside the
gate and still publishes fully-initialized before the state becomes reachable. The sweep-side change is
only the activity comparison moving to bucket space (§3.3) plus the one cache-clear line in
`RemoveCandidateAt`.

### 2.7 API split, and the claim-path ABA left as a recorded defect

- `TryResolveWarm(FlowKey, out FlowStateView)` — **new**, the lock-free warm probe of §2.3. The
  dispatcher's warm entry (`FlowDispatcher.cs:163`) calls it.
- `TryResolve(FlowKey, out FlowState?)` and `TryClaimResolved(...)` — **signatures unchanged** (gated
  authority + write-through). This is deliberate: it keeps every existing call site, both 0 B gates and
  the whole test surface compiling untouched, and it keeps the mechanism's diff to the table plus the
  dispatcher's warm line.
- Consequence, recorded rather than papered over: the dispatcher's *slow* path still reads the pooled
  instance after the gate is released (`FlowDispatcher.cs:203-243`), i.e. the pre-existing latent
  post-gate ABA on the claim/slow path is **not** fixed by this mechanism (the warm half is, because the
  warm probe returns a validated view). Fixing it needs the view API on the claim path, which churns ~15
  test call sites and is not required by the PRD; it stays on the follow-up list (`design.md` §12 D12,
  notes §13.4).


## 3. Activity time: one bucket, published once per pump iteration

### 3.1 The clock

```csharp
// WinForward.Core/ActivityBucket.cs
public static class ActivityBucket
{
    /// 500 ms. The binding half of the F3 contract is "≥8 buckets per retention window"; the smallest
    /// accepted idle timeout is udpSessionIdleSeconds = 5 (ConfigurationLimits.cs:58), whose /8 is
    /// 0.625 s, so the F3 item 4 lower bound of "≥1 s" cannot hold at the configuration floor.
    public const long TicksPerBucket = TimeSpan.TicksPerMillisecond * 500;
    public static long FromUtcTicks(long utcTicks) => utcTicks / TicksPerBucket;
    public static DateTimeOffset ToUtc(long bucket) => new(bucket * TicksPerBucket, TimeSpan.Zero);
}

public sealed class ActivityBucketClock(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long _bucket = ActivityBucket.FromUtcTicks((timeProvider ?? TimeProvider.System).GetUtcNow().UtcTicks);
    public long Current => Volatile.Read(ref _bucket);                 // no clock call
    public long Tick() { ... Volatile.Write(ref _bucket, next); ... }   // exactly one clock read
    public long Publish(DateTimeOffset utc)                             // no clock read: uses the argument
        { var bucket = ActivityBucket.FromUtc(utc); Volatile.Write(ref _bucket, bucket); return bucket; }
}
```

**Tick sources, and where the clock is reachable (the seam, named):**

1. **The capture pump's per-iteration callback.** `NdisCapturePumpOptions.OnBatchCompleted` is invoked
   once per `RunIteration`, including empty polls and the loop-exit flush (`NdisCapture.cs:286-296`,
   `:308`, `:222`); `CapturePacketProcessor.OnBatchCompleted` is the composition's per-iteration seam
   (`CapturePacketProcessor.cs:35-41`, wired per adapter at `MultiAdapterCaptureLoop.cs:34-46`). The
   **only** chain point, and therefore where the tick lands, is
   `Program.cs:284` — `new CapturePacketProcessor(bundle.Dispatcher, logger, bundle.FlushPendingInjections)`
   — so the tick is added inside `DurableCaptureBundle.FlushPendingInjections`
   (`DurableCaptureBundle.cs:362`) next to the redirect-lane flush. Cost: one clock read per pump
   iteration (~1 ms cadence) instead of one per packet.
2. **Every claim** (`FlowTable.TryClaimResolved` → `Reset`): one clock read per claim makes a new flow's
   first stamp exact, fixes F3's D9 by routing `Reset` through the injected clock, and gives a second
   refresh source on any tree where the pump callback is not wired (tests, benchmarks, harnesses).
3. **Every sweep tick** (`RemoveExpired(now, …)` calls `Publish(now)` before scanning): the sweep then
   always compares against the bucket derived from **the clock it was handed**, published from the same
   argument — no second clock read, and the two cannot drift apart within the call.

**One clock instance, threaded explicitly.** `FlowDispatcher` builds its `FlowTable` with neither a
`TimeProvider` nor a clock today (`FlowDispatcher.cs:113`), and the coordinators are built by
`TcpProxyComposer`/`UdpProxyComposer` from `DurableCaptureBundle` (`DurableCaptureBundle.cs:188-244`), so
the clock is created **once in `DurableCaptureBundle`** from the bundle's `TimeProvider` and passed:
into `FlowDispatcher` (new optional ctor parameter → its `FlowTable`), into the `TcpRedirectTable`
(constructed in the bundle before the composer), and into `UdpProxyComposer`/`UdpProxySession` through the
session context. Tests construct their own clock around their `MutableTimeProvider`; the frozen-bucket
risk test drives the **composition** (a `DurableCaptureBundle` with a fake capture loop, asserting one
tick per iteration), not just a pump in isolation.

**This is a deviation from F3 contract item 3's wording** ("one bucket per batch/queue iteration carried
on `CapturedFlowPacket`/`FlowContext`"). The property the contract exists to guarantee is preserved
(exactly one clock read per iteration, zero per packet); the mechanism avoids a packet-shape change that
would reach `NdisCapturedPacket` (NdisApi), the classifier, `CapturedFlowPacket` and every test and
benchmark that constructs one. Recorded as a discrepancy rather than silently substituted.

### 3.2 Storage and the public surface

| Type | before | after |
|---|---|---|
| `FlowState` | `DateTimeOffset LastActivityUtc { get; private set; }`; `Touch(DateTimeOffset)` | `long _activityBucket`; `LastActivityUtc => ActivityBucket.ToUtc(Volatile.Read(...))` (derived); `Touch(DateTimeOffset)` kept (writes the bucket); `internal TouchBucket(long)`; `internal ActivityBucketForDiagnostics` |
| `TcpRedirectAssociation` | `DateTimeOffset LastActivityUtc` written by `Touch(now)` | same public shape, bucket-derived; `Touch(DateTimeOffset)` writes `ActivityBucket.FromUtc(now)`; `internal BucketForDiagnostics` |
| `UdpProxySession` | `long _lastActivityTicks` from `_timeProvider.GetUtcNow()` per send | `long _lastActivityBucket` from the clock's published bucket (no clock call); `LastActivityUtc` derived |
| `UdpAssociation` (unchanged) | `DateTimeOffset LastActivityUtc` | unchanged; the session's observer passes `ActivityBucket.ToUtc(bucket)` |

`UdpProxySession`'s 100 ms propagation rate limit (`s_activityPropagationInterval`) becomes a bucket
distance (`1` bucket ⇒ at most one propagation per 500 ms) — the documented intent is "coarser than
per-operation, unobservable at seconds-scale idle timeouts", and the new granularity is coarser still.
The propagation's "never propagated" sentinel **must not be `0`**: bucket 0 is a real bucket (any instant
in `[1970-01-01, +500 ms)`), and `UdpProxySessionTests` starts its clock at `UnixEpoch` (`:23`, `:38`), so
a `0` sentinel would suppress the session's first propagation and break the documented "first activity
propagates immediately". Use `long.MinValue` (or an explicit `bool`), and pin it with the existing
first-propagation fact.

`UdpAssociation`'s stored representation stays `DateTimeOffset` (F3 item 5), but the justification is
narrower than "its idle sweep absorbs the quantisation": `UdpAssociationPool`'s idle retirement is
**lease-based**, not activity-based (`UdpAssociationPool.cs:75` `s_idleRetireTimeout` = 60 s;
`CanRetire` reads `UdpControlAssociation._idleSinceTicks`, `UdpControlAssociation.cs:175-181`), and the
only reader of `UdpAssociation.LastActivityUtc` in this tree is the test-only
`UdpAssociations.cs:138` (`UdpAssociationTable.RemoveExpired`, no production caller). So the honest
statement is: **the session's observer writes a bucket-derived stamp that can be up to 500 ms older than
the true activity instant; nothing on a production idle path consumes it.** The never-early claim below
covers the legs that *do* consume activity on a production path — the flow table, the TCP redirect
association (table + session store), and the UDP session's own `TryBeginExpiry`/coordinator scan.

### 3.3 Expiry, in bucket space, never early

The sweep computes the cutoff once per call and each entry compares integers, exactly as F3 landed it —
only the scale changes:

```csharp
var cutoffBucket = idleTimeout <= TimeSpan.Zero
    ? long.MaxValue                                                      // the zero/sub-bucket rule, below
    : ActivityBucket.FromUtcTicks(now.UtcTicks - idleTimeout.Ticks);     // once per call, from the argument
// expired ⟺ state.ActivityBucket < cutoffBucket          (FlowTable.ScanChunk / RemoveCandidateAt)
//           ⟺ association.Bucket  < cutoffBucket          (TcpRedirectTable.RemoveExpired :342/:355, and the
//                                                           TcpRedirectSessionStore scan :134 + re-check :150)
```

**The operator is `<`, not `<=`.** A stamp is the bucket its instant falls in, i.e. quantised *down*;
`<=` would retire a state up to one bucket **early**. With `<`, retirement age for a stamp taken at `t0`
is `idleTimeout + w − (t0 mod w)`, i.e. in `(idleTimeout, idleTimeout + w]`: never early, at most 500 ms
late, and therefore inside the existing "idleTimeout plus one sweep interval" envelope with 500 ms of
slack (the UDP leg's interval is ≥5 s, the main leg's is 60 s). The comparison is always on the **internal
integer bucket**, never on `LastActivityUtc` — a per-entry `DateTimeOffset` compare would contradict F3
contract item 7 and reintroduce the arithmetic the cutoff exists to avoid.

**The zero/sub-bucket rule.** A non-positive `idleTimeout` keeps today's meaning — *retire everything on
this call* — via `cutoffBucket = long.MaxValue`; without the rule a state stamped in the current bucket
would survive to the next edge (≤500 ms), and three call sites depend on the drain semantics:
`UdpChurnScenario.cs:234` (a drain loop that throws unless `SessionCount == 0`),
`UdpSessionBudgetRun.cs:97`, and `SessionSetupDecompositionBenchmarks.cs:249` (`Assert(removed == Sessions,
"T3 must retire every session in one expiry sweep")`). Positive-but-sub-bucket timeouts need no special
rule: the derivation above holds for any `idleTimeout > 0`, so the envelope is `(idleTimeout, idleTimeout
+ w]` even at a 50 ms test timeout. **Do not "tidy"** `SweepAllocationGateTests`' `DateTimeOffset.UtcNow
.AddSeconds(1)` sweep instants (`:78`, `:88`, `:177`, `:183`, `:196`): 1 s is two buckets, so they cross an
edge and stay exactly-expired; the `now − 2 min` / `now` pair in the same class is 240 buckets apart.

The exact boundary moves: a state stamped at `t0` and swept at exactly `t0 + idleTimeout` is **retained**
and retired at the first sweep at or after the next bucket edge. Four assertions pin the old exact
boundary (`CoreFlowStructuresTests.cs:96-97`, `:116-117`, `:133-138`) and move to the bucket form; this is
the churn F3 predicted when it deferred F3.4, and it is the *safe* direction (late, not early).

## 4. Self-traffic: the exact half leaves the warm path, the wildcard half stays (lock-free)

The registry answers two questions that must be split, not moved wholesale (PRD requirement 2):

| Half | Key probed | Warm path | Claim path |
|---|---|---|---|
| **exact tuple** | `_entries[SelfTrafficKey(protocol, local, remote)]` + its reverse — the relay/control socket's own 4-tuple | **removed** | full check, once per claim (`FlowDispatcher.cs:194`) |
| **wildcard** | `_wildcards[WildcardKey(protocol, localPort, remote)]` + its reverse — a registration whose local address is `0.0.0.0`/`::`, i.e. "any local address on this port to this remote" (`SelfTrafficRegistry.cs:19`, `:35`, `:55-59`) | **stays**, answered lock-free | also checked (it is part of `IsOwned`) |

Why the wildcard half cannot leave, with the code path: `TcpProxyRelay` registers the **upstream control
connection** as a wildcard before its SYN leaves the host — the socket is bound to a wildcard local
endpoint, so the registration is `(Tcp, Any:P, proxyEndpoint)` and the wildcard matcher covers the
routing-chosen source IP (`TcpProxyRelay.cs:36-44`, comment in-tree; this mirrors the UDP relay). A host
flow to the SOCKS5 endpoint that was claimed on ephemeral port P leaves a `Proxy` state in the flow table;
the port is later recycled to a relay's control socket, which registers `(Tcp, Any:P, proxyEndpoint)`.
With the whole check gone from the warm path, that relay's SYN resolves the stale `Proxy` state and
WinForward's own control connection is redirected into its own proxy — recursively, and **durably**,
because every warm hit re-touches the state. Loop prevention is a fail-closed contract
(`traffic-policy-lifecycle.md`), so the wildcard half stays.

**Mechanism.** `_wildcards` becomes a `ConcurrentDictionary<WildcardKey, long>` (mutations keep running
under `_gate` with the existing generation check, `SelfTrafficRegistry.cs:37-47`); a new interface member
answers the warm question with no lock at all:

```csharp
public interface ISelfTrafficGuard
{
    /// Warm-path half: only the wildcard relay-socket registrations. Lock-free; ≤2 probes.
    bool IsWildcardOwned(FlowContext context);
    /// Full check (exact + wildcard), claim time only.
    bool IsOwned(FlowContext context);
}
```

`FlowDispatcher.DispatchAsync` replaces its `IsOwned` call at `:162` with `IsWildcardOwned`; the claim-time
`TryHandleSelfTrafficAsync` (`:194`, `:246-252`) keeps the full `IsOwned`, unchanged and still reached
before the reverse hook and before `TryClaimResolved` for every packet that reaches the slow path.
`DispatchNonFlowAsync:330` is untouched: non-flow frames have no table entry, and the full check there is
not per-established-flow work.

**What is cached, and what the delta actually is.** A claimed state is the "proven not self" record for the
*exact* half: a self-owned exact tuple never produces a state, because the claim-time check runs first. The
accepted delta is therefore narrower than "a self-registered tuple keeps proxying until the flow expires":
every warm hit re-touches the state, so an already-claimed flow whose exact tuple is registered as self
later keeps being proxied **for as long as it keeps receiving packets** (the state only expires after the
flow idle timeout of silence, `IdleExpirySweeper.cs:59` = 5 min default). Registration ordering bounds the
reachable cases, and it is **not uniform** — both sites must be named in the record:

- `TcpProxyRelay.cs:36-44` registers the relay's control tuple **before its SYN leaves the host** — that is
  the ordering contract that makes a relay's own traffic uncatchable, and it is the case the wildcard half
  now covers on the warm path.
- `TcpRedirectSetup.cs:179` (`RegisterSessionAsync`) registers the *translated* listener tuple
  `(Tcp, translatedTuple, translatedTuple)` **after** the original flow was claimed in the same pipeline
  (`table.TryClaim` at `:66`). That registration is not a wildcard unless the listener address is any, so
  it is covered only by the claim-time check: a packet whose key *is* the translated tuple and arrives
  after the original claim but before/around this registration would be proxied rather than passed. It is
  the same pipeline step (microseconds), and the pre-change behaviour for it — abruptly un-proxying after
  the fact — is not obviously better; recorded as the residual the reviewer should attack, with the fact
  that today's next-packet diversion is what changes.

**Proof.** Two facts, both exact:

- `WarmHitTakesZeroExactTupleGuardProbes` — a counting guard: N warm dispatches on a claimed flow ⇒ 0
  *exact-tuple* probes; M distinct keys through the dispatcher ⇒ exactly M full checks (one per claim).
- `ARelayWildcardTupleIsNeverProxiedOnAWarmHit` — the regression pin for the finding above: claim a host
  flow on local port P to the proxy endpoint, keep it live, register `(Tcp, Any:P, proxyEndpoint)` the way
  `TcpProxyRelay` does, dispatch a packet on that tuple, and assert the dispatcher takes the self-traffic
  pass path and never the proxy path (`executor.ProxyCount == 0`).
  **Honest red-before shape**: this fact is *green on the unmodified tree* (the old warm entry runs the
  full `IsOwned`, which already covers the wildcard half) and green on the fixed tree; it is **red against
  the naive variant** this step is built from — delete the warm-entry check outright, as the PRD's
  requirement 2 originally read — which the step must run and record under `warm-path-gate-counts.txt`
  before landing the split. The PRD's "red before the change" is therefore discharged by that recorded
  variant, not by the unmodified tree; the design records the distinction rather than claiming a red the
  tree cannot produce.

`IsWildcardOwned` is also what the *scaling* benchmark's warm arm must call (§10.2) — it is part of the
production warm shape now, and it must appear in the measured unit.

## 5. TCP redirect table: one probe, no gate on the packet path

- The four dictionaries stay the **gated authority** (`_byOriginal`, `_byReverse`, `_byTranslatedListener`,
  `_byAddressPair`): the concurrent-index shape is rejected everywhere for the same 488 B/claim reason as
  §2.0 (`TcpRedirectTable` would allocate nodes for each of `_byOriginal`/`_byReverse`/`_byTranslatedListener`
  per claim and a fourth index write), and a plain `Dictionary` read outside the gate is unsafe. All
  mutations keep running under `_gate`, so the exactly-once alias invariants are unchanged: a claim still
  checks both indexes and the capacity inside one hold, and a removal still publishes the tombstone inside
  the same hold (`TcpRedirectTable.cs:345-360`).
- **Two pre-allocated direct-mapped caches** replace the two warm probes, each allocated once in the ctor
  as `TcpRedirectAssociation?[slots]` with
  `slots = RoundUpToPowerOf2(clamp(capacity * 8, 1_024, 16_384))` (≤128 KB at the default redirect capacity):

  | cache | slot index | validation (exact, from immutable fields) |
  |---|---|---|
  | reverse | `HashCode.Combine(source, destination) & mask` (the association's `ReverseSourceEndpoint`/`ReverseDestinationEndpoint`) | `assoc.ReverseSourceEndpoint == source && assoc.ReverseDestinationEndpoint == destination` |
  | original | `FlowHash`-style mix of the full `FlowKey` (`key.GetHashCode() & mask`) | `assoc.OriginalKey.Equals(originalKey)` |

- **No validation flag is needed on the association**, unlike `FlowState`: `TcpRedirectAssociation` is
  never pooled or recycled (`:240` allocates a fresh one per claim) and the two validated field pairs are
  get-only, set in the ctor and never rewritten — so a cache entry is always the association the slot
  named and never a *different* one: an unpopulated slot or a collision fails validation and falls back
  to the gated authority. It can still be *stale*: a probe that loaded the reference before
  `RemoveUnderGate`'s guarded clear returns that association, because the index delete and the cache
  clear are not one atomic step with an in-flight probe. That is the same one-probe window the
  pre-change ordering had between the gated resolve and the out-of-gate rewrite, and it is recorded as
  a residual (§8 row 3/12) rather than claimed away. Using a just-removed association is a race
  that already exists today: `HandleReverseAsync` resolves under the gate and then rewrites and injects
  outside it (`Injections.cs:305-380`). Reading `Phase` after a lock-free probe is not new either — the
  warm reverse path already reads the association after the gate is released.
- **Population / invalidation, named**: write both slots in `TryClaim` after the four index writes
  (`:250-256`); write-through on a gated hit in `TryResolveByReverse` (`:275-285`) and
  `TryResolveByOriginal`/`TryFind` (`:420-432`); clear both entries in a **single factored removal helper**
  used by `TryRemove` (`:345-360`) and by both `RemoveExpired` removal bodies (`:389-400`) — factoring the
  removal is part of this step precisely so a fourth removal site cannot be added later without the cache
  clear (the current tree duplicates the four-index removal twice).
- `IsReverseCandidate` (`:289-292`) is deleted, and the fold is **call-site complete**, not just a deleted
  method: `HandleReverseIfApplicableAsync` needs the probe to decide the tombstone branch
  (`TcpProxyCoordinator.cs:359-367`) and `HandlePacketAsync` needs it to choose the reverse path over the
  SYN/data legs (`:391`). Both must therefore call `TryResolveByReverse` **once themselves** and pass the
  association into `HandleReverseAsync`, whose own probe (`Injections.cs:305`) is deleted. If only the
  candidate check is deleted and `:305` stays, a warm reverse packet pays **two** probes and the 2→1 claim
  fails; the exact fact counts probes, not gate entries, so it would catch that.
  `IsReverseCandidatePort` (`:300`) keeps its role as the dispatcher's warm prefilter, so `WantsPacket`
  stays lock-free and a port-prefilter miss still falls through to the slow path's full check (the
  fall-through theorem). The prefilter is a candidate filter: a forward flow whose local port number is
  itself a live listener port (both are OS-assigned ephemerals) takes one gated reverse probe per packet
  while that listener exists, then falls through to the original-index probe — one gate entry fewer than
  the pre-change pair, and the honest reading of the zero-gate claim for the forward direction.
- **`TryResolveByAddressPair` stays gated.** `_byAddressPair` remains a `Dictionary` read under `_gate`
  (`:282-296` … `:486-500`): it serves the fragment path (`TcpProxyCoordinator.cs:441`), which is not the
  warm packet path, and the address-pair index is written by every claim — a cache there would add a third
  array for no measured win.
- `TcpRedirectTable.Count` keeps reading `_byOriginal.Count` under `_gate` (`:202-208`): with the
  `Dictionary` authority unchanged, that stays exact and cheap — **no maintained counter is needed** (the
  counter rule of the rejected concurrent-index variant no longer applies).
- Tombstone ordering is preserved because it is keyed on the reverse-index *miss*
  (`TcpProxyCoordinator.cs:364-366`, `:419`), which the fold keeps identical.
- The four resolve methods keep their `DateTimeOffset now` parameters (callers pass the bucket-derived
  timestamp), so `TcpRedirectTable`'s public surface is unchanged apart from deleting
  `IsReverseCandidate`; only the internal comparison changes to bucket space (§3.3).

## 6. UDP ready path: wait-free, no clock read

### 6.1 The reorder (the load-bearing step)

Today the cooldown probe and the clock read happen **before** the session lookup
(`UdpProxyCoordinator.Send.cs:30-37`). The order is inverted:

```csharp
internal ValueTask<bool> TrySendSpanAsync(FlowKey flow, Socks5Server server, ReadOnlySpan<byte> payload,
    MacAddress clientMac, CancellationToken cancellationToken, long packetSequence = 0, long flowGeneration = 0)
{
    if (_sessions.TryGetValue(flow, out var slot) && slot.Ready && slot.Session is { } session)
        return SendOnReadySessionSpanAsync(flow, slot, session, payload, packetSequence, cancellationToken);  // no gate, no clock
    return SendAdmissionPathSpan(flow, server, payload, clientMac, cancellationToken, packetSequence, flowGeneration);
}
```

`SendAdmissionPathSpan` is the old body: it takes `_gate`, reads the clock once, probes the cooldown,
checks `_sessionCount >= Capacity`, admits a new slot (`ScheduleSessionSetup` + add), **re-checks
`slot.Ready` under the gate** (a datagram first read as "not ready" must not be enqueued after the flush
already drained the queue and flipped the slot ready), and otherwise enqueues into the setup queue.

Why moving the cooldown probe is semantics-preserving, not a shortcut: `_cooldowns.Write` has exactly
two call sites — `UdpProxyCoordinator.cs:434`, inside the same `_gate` hold that removed the slot at
`:420`, and `Clear()` at dispose (`:243`) — so a flow in cooldown has no slot, and the admission path
re-checks the cooldown before creating one. "Ready session ∧ cooldown" is therefore unreachable, and a
ready hit that skips the probe cannot change any outcome. Requirement 5's "cooldown reads are lock-free"
is met in the stronger form *no cooldown read on the ready path at all*.

### 6.2 Lock-free lookup and safe publication

- `_sessions` **stays the gated `Dictionary` authority** (the concurrent-index shape is rejected for the
  same 488 B/claim reason, and here it would additionally break
  `HotPathAllocationGateTests.UdpSetupEnqueuePathAllocatesNoManagedBytes`, whose window contains the
  admission's dictionary insert). `Count` (`UdpProxyCoordinator.cs:105`), capacity and disposal all keep
  reading it under `_gate` — **no maintained counter is needed**.
- A **pre-allocated direct-mapped session cache** carries the ready path:
  `private readonly UdpSessionSlot?[] _sessionCache` allocated once in the ctor with
  `slots = RoundUpToPowerOf2(clamp(capacity * 8, 1_024, 16_384))` (≤128 KB at the default session
  capacity). Slot index = the flow key's hash (`FlowHash`/`GetHashCode() & mask`; the coordinator keys on
  the exact flow, so no canonical ordering is needed here). Validation is exact and comes from the slot
  itself: `Volatile.Read(ref slot.Session) is { } session && session.Flow.Equals(flow)`. A slot whose
  session is not yet attached (or a colliding flow's slot) fails validation → the cold path → `_gate`
  (which also re-checks `Ready` after taking the gate, §6.1).
- Population / invalidation, named: write the slot at the admission add (`Send.cs:62`), re-write it at the
  ready flip (`DequeueForFlush`, `UdpProxyCoordinator.cs:368-384`, under `_gate`), clear it on
  `RemoveSlotAsync`'s removal (`:460-462`) and on the dispose drain's `_sessions.Clear()` (`:276`) — the
  clear is `if (ReferenceEquals(_sessionCache[i], slot)) _sessionCache[i] = null` under `_gate`, never a
  blind wipe of another flow's entry.
- `UdpSessionSlot.Session`/`Ready` get volatile accessors. Publication order is already correct today —
  `AttachSession` writes `Session` under the gate (`:340`), the flush loop drains and then sets
  `Ready = true` under the gate (`:375`) — so a reader that validates `Session.Flow` and then reads `Ready`
  is guaranteed the pair by the release/acquire ordering. The two writes are simply made explicitly
  volatile.
- The stale-entry case is today's existing race, not a new one: a reader that loaded the cache entry
  before a teardown clears it may use a slot whose session is expiring or disposed → `SendSpanAsync`
  returns `false` → the counted `UdpFailClosedDrop` (`Send.cs:150-155`), exactly what the authoritative
  path does when it resolves the slot a moment before the removal.

### 6.3 The session's send and touch paths lose `_activityGate` and the clock

```csharp
public ValueTask<bool> SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken ct)
{
    if (Volatile.Read(ref _expiring) || _scope.Fault is not null || !_scope.TryEnter(out var workLease))
        return ValueTask.FromResult(false);            // fail-closed, counted by the caller
    ...
}
private void TouchActivity()
{
    var bucket = _activityBucketClock.Current;                       // no clock call
    if (Volatile.Read(ref _expiring)) return;
    Volatile.Write(ref _lastActivityBucket, bucket);
    ... propagate to the association table at most once per bucket ...
}
```

`QuiescenceScope.TryEnter` (`QuiescenceScope.cs:78-95`) is already a lock-free CAS and is the real
admission authority; the scope's drain joins outstanding leases, so a sender admitted before the seal is
waited for. `_activityGate` is retained for `TryBeginExpiry`/`CancelExpiry`/`State`, i.e. only the
lifecycle transitions. `TryBeginExpiry`'s idleness test moves to bucket space with the same never-early
operator (§3.3).

The race that the lock used to narrow (a sender admitted concurrently with the sweeper's `_expiring`
decision) **is widened, and the widening is an accepted delta with a name**: today a sender that reaches
the check before the sweeper stores `_expiring` is refused at the check or (if it already passed) is
inside; after the change there is a real window in which a sender reads `_expiring == false` between the
sweeper's `IsIdle` re-check (`UdpProxySession.cs:198`) and its `_expiring = true` store (`:199`), so the
datagram **goes out** instead of being counted as a `UdpFailClosedDrop` (`Send.cs:150-155`,
`RuntimeCounters.UdpFailClosedDrop`). That is the benign direction (a datagram on a session that is about
to expire is delivered rather than dropped, and `DrainAsync` joins its lease, so disposal cannot free the
transport under it), but it changes a counted drop into a send and must be recorded in §7's accepted delta
list and pinned by a test that drives the sweeper's re-check concurrently with a send.

**The other two bucket legs at this site, stated so they are not missed:**

- `TryBeginExpiry(now, idleTimeout)` (`UdpProxySession.cs:194-207`) — the `now - LastActivityUtc <
  idleTimeout` guard becomes the bucket form with the never-early operator (strict `<`), under the same
  `_activityGate` it already holds.
- `UdpProxyCoordinator.RemoveExpiredAsync`'s candidate scan (`UdpProxyCoordinator.cs:369`,
  `session.LastActivityUtc` comparison) and its re-check — the scan may stay in `DateTimeOffset` space
  **only as a pre-filter**, because `TryBeginExpiry` re-checks in bucket space; to avoid two comparison
  shapes, move the scan to the bucket form as well (a pre-filter that is one bucket *older* than the
  re-check is safe: it can only add candidates, never lose them).

### 6.4 The 0-gate-entry proof consumes the Step 1 probe

`UdpProxyCoordinator.GateEntryCountForDiagnostics` counts `_gate` acquisitions and
`UdpProxySession.ActivityGateHoldProbe` counts `_activityGate` acquisitions; the post-change fact
`UdpReadySendTakesZeroActivityGateEntries` asserts **both stay flat** across N ready-path sends (zero
coordinator gate entries *and* zero activity-gate entries), which is what makes the dropped lock a
*proved* removal rather than a code review claim.

## 7. Contracts

### Public — unchanged

`FlowTable.Capacity`, `FlowTable.Count`, `FlowTable.RemoveExpired(now, idleTimeout, isHeld)`,
`TcpRedirectTable`'s claim/resolve/remove/expiry signatures (minus the deleted `IsReverseCandidate`),
`TcpRedirectAssociation`'s public surface, `UdpProxyCoordinator.SessionCount`/`Capacity`/`SessionState`/
`RemoveExpiredAsync`/`TrySendSpanAsync`'s signature, `UdpProxySession.LastActivityUtc`, the whole
`IUdpSessionSlotHost` seam, `ISelfTrafficGuard`, and `FlowDispatcher`'s public surface.

### Changed

- **New**: `FlowTable.TryResolveWarm(FlowKey, out FlowStateView)` — the lock-free cache probe (§2.3),
  called by the dispatcher's warm entry.
- `FlowTable.TryResolve` / `TryClaimResolved` keep `out FlowState?` and their gated semantics (the
  authority + write-through); `FlowStateView`/`FlowState.TrySnapshot` from Step 2 are the warm probe's
  validation (no test call site, no 0 B gate and no diagnostics accessor needs to change — the accessors
  the previous mechanism required are **no longer needed**).
- `ISelfTrafficGuard` gains `IsWildcardOwned(FlowContext)`; `IsOwned` keeps its meaning (full check) and
  its claim-time call site.
- `FlowState.LastActivityUtc`: still a `DateTimeOffset`, now bucket-quantised; `Touch(DateTimeOffset)`
  still accepts any instant and stores its bucket.
- Retention boundary: "expired at age ≥ idleTimeout" → "expired at age ∈ (idleTimeout, idleTimeout+500 ms]",
  with `idleTimeout ≤ 0` keeping "retire everything on this call" (§3.3).
- `FlowDispatcher`'s warm entry calls `IsWildcardOwned` and `TryResolveWarm`.
- TCP redirect table gate entries per packet: 2 → 0 on a cache hit and reverse-index probes per packet:
  2 → 1; UDP coordinator gate entries per ready datagram: 1 → 0 on a cache hit.
- Each table gains one pre-allocated cache array (≤2 MB for the flow table at the shipped capacity, ≤128 KB
  each for the redirect table and the coordinator) — the only memory this mechanism adds, recorded in the
  artifact against the measured 32.5 MB `flowTable` stage.

### New internal members (diagnostics-only, null/absent in production)

- `FlowTable.GateHoldProbe` (`Action?`), invoked immediately after `_gate` is taken in
  `TryClaimResolved`/`RemoveExpired`; `TcpRedirectTable.GateHoldProbe`; `UdpProxyCoordinator.GateHoldProbe`
  — the same shape as `HoldProbe` (`FlowTable.cs:78-83`: "read once into a local … behind a plain null
  check, so a Release run carries it with no allocation"), and off the warm path by construction because
  the warm paths no longer take the gates.
- `TcpRedirectTable.GateEntryCountForDiagnostics` (the probe **count** per warm packet),
  `UdpProxyCoordinator.GateEntryCountForDiagnostics`, `UdpProxySession.ActivityGateHoldProbe`,
  `FlowTable.TryResolveStateForDiagnostics` (identity-based tests; maps to the gated authority).
- Hit/miss counting for the acceptance evidence lives in the **scenario**, not in production: the
  `scaling` scenario counts `TryResolveWarm`'s true/false returns itself, so the warm path stays one
  volatile read with no counter write.

### Accepted semantic deltas (each also in §8)

1. Activity is quantised to 500 ms; the exact idle boundary moves to the never-early form.
2. A tuple whose **exact** registration happens *after* its flow was claimed keeps proxying for as long as
   the flow keeps receiving packets (it is re-touched on every warm hit), instead of diverting on the next
   packet. The **wildcard** half stays on the warm path, so the relay-control-tuple recursion this could
   otherwise cause is *not* in the delta. Both registration sites are named in §4.
3. A warm read that loses the validation race (a concurrent recycle/removal) takes the slow path once —
   the same answer, a slower packet.
4. `Count`/`Capacity` stay exact; `SessionCount` stays exact; no retention envelope widens beyond 500 ms.
5. A UDP send admitted between the sweeper's idle re-check and its `_expiring` store goes out instead of
   being a counted `UdpFailClosedDrop` (§6.3) — a counted drop becomes a delivered datagram; the landed
   fact is `SendIsAdmittedWhileTheSweeperHoldsTheActivityGate` (it parks the sweeper inside the activity
   gate and proves the ready send neither waits nor is refused), **not** the drop→send counter flip: that
   window has no seam and is reported as the task's one acceptance gap (implement.md's session record,
   deviation 3), and the two spec rows describing the old
   order (`udp-relay.md:471`, `async-lifetime.md:182-183`) are restated.

## 8. Semantics and risk table

| # | Condition | What a torn read / stale cache can do | Bound / control |
|---|---|---|---|
| 1 | Torn `FlowKey` read on a recycled `FlowState` | must never yield a decision for a key that was not published | barrier-bracketed version (§2.3) + key/tuple corroboration; failure ⇒ miss ⇒ slow path re-resolves under the gate; the writer publishes odd → fence → fields → fence → even, so the bracket holds on ARM64 as well as x86 |
| 2 | Reader preempted between the dictionary probe and the field read, state recycled | as above | same; this is the pre-existing claim/`TryResolve` defect the view also closes |
| 3 | Reader preempted across a removal | returns the decision the flow had a moment earlier | identical to the pre-change ordering at the expiry boundary; `FlowGeneration` may be one older (log correlation only) |
| 3b | State removed **and re-claimed for the same key** inside the reader's window | view carries the newer publication's decision/generation for the same key | accepted: the view means "the decision currently published for this key", policy is deterministic per key, `Generation` is trace-only (§2.3) |
| 4 | `Touch` lands on a recycled or retired state | writes one bucket into an unreachable object | harmless; the value is the current published bucket, which can be **one bucket newer** than the value `Reset` stored (only ever forward) |
| 5 | Sweep re-checks a candidate whose bucket a concurrent resolve just raised | removal skipped, state survives one more round | existing `RemoveCandidateAt` re-check; "a hold never re-arms activity" preserved |
| 6 | Cache memory and capacity | one pre-allocated array per table: 2 MB for the flow table at the shipped capacity (+6 % against the measured 32.5 MB `flowTable` stage), ≤128 KB each for the redirect table and the coordinator; **no per-claim allocation anywhere** | measured into the artifact (residency census + `gc-soak` anchors) with the array sizes named; the factor/cap in §2.1 is the tunable |
| 7 | Cache miss on a hot flow (collision, overwrite, stale entry) | falls back to **today's exact locked path**; never a wrong decision | the miss rate is measured in the scenario and recorded beside the ratio; §2.5's arithmetic bounds what is acceptable |
| 7b | Cache entry left behind after a removal (invalidation gap) | would serve a removed flow's decision for as long as the entry lives | the clear is in the single removal site (`RemoveCandidateAt`, under `_gate`, before `ReturnState`) for the flow table, the factored removal helper for the redirect table, and the removal + `Clear()` sites for the coordinator; a fact asserts a removed/recycled flow is never served from its old slot (§9) |
| 8 | Clock steps backwards | stamps move back, flows can expire immediately | same as today (the stamp is overwritten with an earlier instant); a monotone guard is a design-review question, not silently added |
| 9 | Bucket clock not ticked (unwired composition seam) | bucket freezes, flows are expired while active | three tick sources (the bundle's per-iteration callback, claims, sweeps; §3.1) + an exact test that drives the **composition** and counts one tick per iteration |
| 10 | Exact tuple registered after the claim | keeps proxying while it receives traffic; correctly not covered by the wildcard half | §4 names both registration sites and the residual; `ARelayWildcardTupleIsNeverProxiedOnAWarmHit` pins the half that must not regress |
| 11 | Bucket width vs a deliberately short configured idle timeout | retention granularity coarser than 1/8 of the window | width is fixed at 500 ms; at the accepted floor (5 s) the window holds 10 buckets; the derivation is in §3.1 and recorded as a restatement of F3 item 4 |
| 12 | TCP reverse packet racing a teardown | resolves an association that is being removed | pre-existing (resolve under the gate, rewrite outside it); unchanged by the fold |
| 13 | UDP ready hit racing expiry/disposal | send on a socket that is being torn down | pre-existing; `_scope.TryEnter` + `DrainAsync` still gate admission and join outstanding work |
| 13b | Send admitted between the sweeper's idle re-check and `_expiring = true` | datagram goes out instead of a counted drop | accepted delta (§6.3); the transport is not disposed under an outstanding lease (`DrainAsync` joins it) |
| 14 | A fake collaborator hiding the reorder | a row built on `NeverOwnedGuard` reports a constant zero delta | the reorder's proof is the exact probe counters, and the scaling arm's shape changes with it (§10) |
| 15 | `idleTimeout ≤ 0` at a drain call site | must still retire everything on that call | explicit `long.MaxValue` cutoff (§3.3); the three call sites are named in the plan |

## 9. Acceptance mapping (each PRD criterion → an exact proof or a named series)

| PRD criterion | Proof | Kind |
|---|---|---|
| Scaling (series, one line — PRD AC 1) | `scaling.contention` **warm arm** (registry populated, per-lookup unit = lock-free wildcard guard + `TryResolveWarm` cache probe, no per-lookup exact-tuple `IsOwned`), **self-normalised** 4-thread ratio ≥ 0.6 over 3 runs **with its own 1-thread arm ≥ 3.00 M/s**, **and the measured cache hit/miss counts recorded beside it** (§2.5); the comparability reading against the recorded 1-thread baseline (3,331,758 /s ⇒ **≥ 7,996,220 /s** at four threads) recorded beside it | series |
| No global lock on the warm resolve (exact) | `WarmResolveCompletesWhileFlowTableGateIsHeld`, `ReverseResolveCompletesWhileRedirectGateIsHeld`, `UdpReadySendCompletesWhileCoordinatorGateIsHeld`: thread A parks inside the gate via `GateHoldProbe`, thread B's warm operation must complete within a bounded wait; recorded red on the pre-change tree | structural, exact |
| | plus the counts: flow-table gate entries per warm cache hit 1 → **0**, redirect gate entries per warm TCP packet 2 → **0** and reverse-index probes per packet 2 → **1**, UDP coordinator gate entries per ready cache hit 1 → **0**; and the **0 B claim/expire gate re-run as this mechanism's acceptance gate** (`FlowTableClaimAndExpireCycleAllocatesNoManagedBytes`, unchanged window and assertion) | exact counts |
| Cache correctness (exact) | `WarmCacheHitServesTheValidatedView` (the returned `FlowKey`/`Decision`/`Generation` equal the authority's), `FlowTableCollidingFlowsFallBackToTheGatedPath` (two keys forced into one slot: the evicted flow still resolves, via the gate, with the right decision — a **false miss**), `FlowTableRemovedFlowIsNeverServedFromItsOldSlot` (remove → the warm probe misses → a re-claim serves the new decision), `FlowTableTransportTupleIsUniqueAcrossOrigins` (the invariant §2.3 depends on) | exact |
| Self-traffic reorder (exact — PRD AC 3) | `WarmHitTakesZeroExactTupleGuardProbes` (0 of N warm hits) and `EveryClaimCallsTheFullGuardExactlyOnce` (M of M), **plus** `ARelayWildcardTupleIsNeverProxiedOnAWarmHit` (the retained half; red against the naive-deletion variant, recorded) and `ASelfOwnedTupleIsNeverClaimed` pinning the fail-closed half | exact count |
| Clock call gone (exact + series) | `ThrowingTimeProvider` injected into `ActivityBucketClock`: `ActivityBucketClockReadsTheClockExactlyOncePerTickAndNeverOnAWarmResolve`, plus `UdpReadyDatagramTakesZeroCoordinatorGateEntriesAndZeroClockReads`, `UdpReadySendTakesZeroActivityGateEntries` and the TCP redirect warm packet's zero clock reads; `ReadActivityClock` retired from `FlowTableProductionShapeBenchmarks` and recorded as obsolete | exact + artifact note |
| UDP ready path (series, one line — PRD AC 5) | `udp-ready-path-contention`, 4 workers: **`ReadySend` mean ≤ 261.7 ns** (≥2× the recorded 523.3 ns) over 3 runs, with the 1- and 2-worker rows recorded beside it; **no 4/1-worker ratio condition** (not in the PRD; the recorded ratio is already 1.45 and it is a host metric) | series |
| Release zero-warning, full suite, format, inspectcode | the four gates in `implement.md`'s validation block | exact |
| Benchmark data recorded | `benchmarks/results/2026-09-30-warm-path-lock-chain/` (README + before/after series + gate-count log) | artifact |

**Spec-row → proof map** (the review's process finding: the PRD mapping alone let four spec contradictions
through). Every row below is a binding contract in `.trellis/spec/backend/` that this design touches; the
plan's Step 7 carries the same table and must update each row in the same commit as the change that
invalidates it:

| Spec row | Today | After this task | Proof / where |
|---|---|---|---|
| `hot-path.md:500` | "expired is `LastActivityUtc.UtcTicks <= cutoffTicks`" | bucket form, **strict `<`**, cutoff from the argument | §3.3 + the never-early fact |
| `hot-path.md:38`, `tcp-local-redirect.md:166`, `tcp-local-redirect.md:24` | `IsReverseCandidate` + tombstone on miss | one `TryResolveByReverse` probe, same miss semantics | `ReverseResolveCompletesWhileRedirectGateIsHeld` + the probe count |
| `traffic-policy-lifecycle.md:48` | "`UdpProxySession` tracks `LastActivityUtc` on send and receive" | tracked as a bucket-derived stamp (quantised) | `UdpReadySendTakesZeroActivityGateEntries` + the session tests |
| `udp-relay.md:471` | "lease taken under `_activityGate`, released exactly once in the send tail" | lease taken **without** `_activityGate` (scope CAS only), released exactly once in the tail | `UdpReadySendTakesZeroActivityGateEntries` + the recovery suite |
| `async-lifetime.md:182-183` | "an owner may hold its own gate across `TryEnter` (e.g. `UdpProxySession` …)" | the example no longer holds a gate across `TryEnter`; the lock-order property is unchanged | same fact + the scope's own tests |
| `quality-guidelines.md:17` | "the coordinator gate remains the single gate for slot state" | the ready path reads the session **cache** plus the slot's volatile `Session`/`Ready`; `_sessions` itself and every mutation/removal/expiry still gate slot state | `UdpReadySendCompletesWhileCoordinatorGateIsHeld` |
| `quality-guidelines.md:21` | "association tables use a single `_gate` for ALL mutating and reading methods" | `TcpRedirectTable`'s warm reverse/original probes are served by the lock-free caches; the four dictionaries stay gated authorities, and every mutation is still under `_gate` | the redirect facts + the alias/exactly-once suite |
| `quality-guidelines.md:22` | "every successful resolution MUST call `FlowState.Touch`" | a *validated* resolution touches; a rejected snapshot defers the touch to the slow path (which re-resolves and touches) | the hot-path 0 B gate + the never-early facts |
| `quality-guidelines.md:44` | observation refreshes activity before an expiry boundary | unchanged in intent, bucket-quantised | `FlowTableRetainsAStateUntilTheBucketAfterItsIdleWindow` |
| `hot-path.md:485-489` (pooling) | a reference held past expiry can observe the next flow | callers never hold the instance; the validated view is the only read | §2.3 + `FlowTableRemovedFlowIsNeverServedFromItsOldSlot` / `FlowTableActivitySurvivesARecycledState` |

Nothing in the table is a wall-clock maximum; every timing figure is report-only and quoted beside the
recorded baseline. The one new *countable* family (gate entries, exact-tuple probes, clock reads) exists
precisely because the previous task's pause metric turned out to be host-dominated.

## 10. Measurement plan

1. **Instrument first (F3's discipline).** Land the new benchmark arm and the probes on the unmodified
   product, run them, and record the before-artifact. The new warm arm has no before-number otherwise,
   and the probes' pre-change counts (registry: 1 gate + 4 probes, of which 2 are the wildcard half; flow
   table 1 gate; redirect 2 gates + 2 probes; coordinator 1 gate + 1 clock; session 2 activity-gate
   entries + 1 clock) are the numbers the change must move.
2. **`ScalingContentionScenario`'s third arm** (`warm`, already landed in Step 1): the real registry
   populated (32 exact tuples) and the per-lookup unit = `!guard.IsWildcardOwned(ctx) && table.TryResolveWarm(key, out _)`
   — the post-reorder production shape, wildcard half included. The existing `fake`/`real` arms stay
   unchanged and report-only, so the before/after delta is attributable to the reorder rather than to the
   arm's disappearance. The verdict row gains `warmResolve` with the same fields plus
   `ratioVersusRecordedBaseline = rps(4) / (4 × 3_331_758.3)`, **and the arm counts its own cache
   hits/misses** (the warm probe's return value) so the ratio is attributable to the measured miss rate.
3. **Arm-count arithmetic.** `windowSeconds = Math.Max(1, options.DurationSeconds / (2 * threads.Length))`
   (`ScalingContentionScenario.cs:41`) hardcodes two arms; with three the count must become
   `arms.Length * threads.Length`. The before/after series then run `--quick --duration 63` so each
   configuration keeps the recorded 7 s window (3 arms × 3 thread counts × 7 s); `--duration 45` with the
   corrected divisor would silently shorten every window to 5 s and break comparability with the recorded
   series.
4. **The PRD's one-line criterion, read exactly.** Acceptance is the warm arm's **self-normalised**
   4-thread ratio ≥ 0.6 **and** its 1-thread arm ≥ 3.00 M/s; the ratio against the recorded one-thread
   baseline (⇒ ≥ 7,996,220 /s at four threads) is recorded beside it as the comparability reading. Both
   are printed in the verdict row by the scenario itself, so neither can be re-derived by hand from a
   different arm.
5. **Three runs per series**, median quoted, each run's own `--output` (the runner truncates per process;
   concatenate from temp files as F3 did), full command lines and the metadata row's parameters in the
   artifact README. Timing rows carry `gated: false`.
6. **Retire `ReadActivityClock`** from the production-shape benchmark and say so in the artifact README;
   add a `ResolveWarmHit` row so the post-change shape has a series (the retired row's 40 ns is the
   headroom the bucket buys, not a delta to measure twice).
7. **Must-not-move series**: `tcp-redirect-data-path` (16 rows), the two `FlowTableProductionShape` hit
   rows, `gc-soak` shape anchors and the UDP retention/burst scenarios — recorded, not gated, except
   where an existing gate already covers them.
8. **Gate-count log** (`warm-path-gate-counts.txt`): before/after counts from the probes, the
   naive-deletion red for the wildcard fact, and the measured memory pair (the recorded 32,513,544 /
   32,519,352 / 32,525,832 B floor vs the new floor), with the run command and the tree revision, so a
   reviewer can re-derive every "exact" claim in §9.
9. **The rejected variant's numbers are kept as the recorded comparator**, not deleted: the warm arm's
   three before-runs (0.161 / 0.154 / 0.153, 3.29 M/s at four threads), the concurrent-index patch's
   ratio (0.628) and four-thread rate (11.06 M/s) and its gate failure
   (`Expected: 0, Actual: 124,928` over 256 cycles = 488 B/cycle), and the landed cache's ratio, rate,
   hit/miss counts and gate re-run. The artifact README states which numbers came from which tree — the
   0.628 is the **ceiling this mechanism is trying to approach**, not a result of the landed code.

## 11. Rollback shape

The change has no feature flag and does not need one: every step is independently revertable and the
observable contract changes are confined to three points (the warm-probe API, the activity
representation, the self-traffic split). Per step, revert points are in `implement.md`; the shape is:

- Step 1 (instrumentation, guards, probes, the warm arm) and Step 2 (`ActivityBucket` +
  `ActivityBucketClock` + the `FlowState` seqlock) are **landed and product-neutral** apart from the
  bucket's retention quantisation; they stay if the cache is reverted, and Step 2's `FlowStateView` +
  `TrySnapshot` is exactly what the cache reader needs.
- Step 3 (the warm cache) reverts alone: delete the array, the `TryResolveWarm` call site and the three
  write/clear points; the gated `Dictionary` path is untouched underneath, so a revert is behaviour-zero.
  The concurrent-index variant of the same step is already reverted and must **not** be restored
  (`design.md` §2.0).
- Step 4 (self-traffic split) is one interface member plus one call-site change and reverts alone; the
  wildcard half never changes, so a revert restores the full check on the warm entry.
- Step 5 (redirect caches + the fold) and Step 6 (UDP reorder + session cache) each revert alone; within
  Step 6 the reorder and the cache can be reverted separately (the reorder is semantics-preserving on its
  own, the cache is the wait-free half).
- The benchmark arms and the probes are left in place on any revert; the artifact README records which
  half of each series is post-change.

## 12. Recorded discrepancies (code wins; full list in `research/implementation-notes.md` §9)

| # | PRD / research claim | Code reality | Disposition |
|---|---|---|---|
| D1 | req 1 is reachable by reordering the existing lookups | the indexes are `Dictionary`; a concurrent read throws `InvalidOperationException` on the torn chain (`Dictionary.cs:1255-1290` publishing `_buckets` at `:1275` and `_entries` at `:1290`, read as two fields at `:414-415`; the throw at `:479`) and from a pump thread that tears capture down | restated (and amended into the PRD req 1): the authority stays a gated `Dictionary`, and the warm read is a **lock-free validated cache probe** over it (§2); the sharded rebuild stays out of scope |
| D1b | the amended PRD req 1 names "a concurrent index (`ConcurrentDictionary` or better)" | the concurrent index was implemented, measured (ratio 0.628, 4-thread 11.06 M/s) and **reverted**: 488 B per claim+expire cycle (3 nodes × ~160 B) fails the exact 0 B claim gate, which the parent programme's PRD forbids regressing | superseded by the operator's decision: the direct-mapped warm cache (§2), which reaches the same lock-free warm read with **zero** allocation on the claim path. The concurrent index is recorded as a measured dead end (`research/implementation-notes.md` §13), not as the mechanism |
| D2 | req 2 "cached on the flow" | the claim-time check already precedes every claim; a self *exact* tuple never produces a state, and the wildcard half stays on the warm path (amended req 2) | no flag; the warm entry calls `IsWildcardOwned` |
| D3 | F3 item 3 "bucket carried on the packet" | the pump already has a per-iteration seam (`OnBatchCompleted`), reached from `Program.cs:284` → `DurableCaptureBundle.FlushPendingInjections`; `FlowDispatcher.cs:113` builds its table with no clock and the coordinators are composer-built, so the clock is created in the bundle and threaded | shared `ActivityBucketClock` created in `DurableCaptureBundle`, no packet-shape change |
| D4 | F3 item 4 "≥1 s and ≤ idle/8" | at `udpSessionIdleSeconds = 5` the halves contradict | width 500 ms; "≥8 buckets per window" is the binding half (sanctioned by the amended PRD req 3) |
| D5 | F3 item 5 "other consumers keep their representation" | the per-packet clock reads to remove are in the TCP coordinator and the UDP session | stored representations kept, their *source* becomes the bucket; the amended PRD req 3 names both consumers |
| D6 | req 4 "one gate entry" | the pair is a redundant `ContainsKey` + `TryGetValue` on the same key, and the fold must also delete `Injections.cs:305` or the probe count stays 2 | met with zero gate entries and one probe; proof is a count |
| D7 | req 5 "at most one `TryEnter` attempt" | `Monitor.TryEnter` would drop datagrams under contention | met with zero attempts (lock-free lookup + cold-path admission) |
| D8 | req 7 "`HotPathAllocationGateTests` stays green" | the concurrent-index variant broke it with 488 B per claim+expire cycle; the landed mechanisms (cache + gated `Dictionary`) leave `TryResolve`/`TryClaimResolved` signatures untouched, so both gates compile and pass **unchanged** | the 488 B is not sanctioned and the gate is not re-baselined: the concurrent index was reverted and is recorded as a measured dead end (§2.0, notes §13) |
| D9 | the criterion's ratio | the scenario self-normalizes against its own 1-thread arm, and `:41` hardcodes two arms | the amended PRD makes the self-normalised ratio the acceptance line with the recorded-baseline reading beside it; the arm count is corrected and the series runs `--duration 63` |
| D10 | unrecorded: pre-existing post-gate ABA on both resolve and claim return values | `FlowDispatcher.cs:165-186`, `:203-243` read a pooled state after the gate is released | fixed by the view; recorded as a defect found here |
| D11 | "the next packet would divert" (the delta's bound) | every warm hit re-touches, so an already-claimed flow with a later exact registration keeps proxying **while it receives traffic**, not "until it expires" | the bound is restated in §4/§7 and the two registration sites are named (`TcpProxyRelay.cs:36-44` before the SYN; `TcpRedirectSetup.cs:179` after the claim) |
| D12 | Not in any source document: the pre-existing post-gate ABA on both `TryResolve` and `TryClaimResolved` return values | see §8.2 | the **warm** half is fixed by the validated cache view; the slow/claim half is left as-is by the operator's Step-3 scope (widening the claim API churns ~15 test call sites) and is recorded in §2.7 as a known, deferred defect with its site named |
| D13 | `idleTimeout == TimeSpan.Zero` means "expire all" | a strict bucket comparison would keep a current-bucket stamp alive to the next edge | explicit `long.MaxValue` cutoff rule; the three drain call sites are named |
| D14 | `ConcurrentDictionary.Count` is usable for capacity/`Count` | it acquires every bucket lock | moot for the landed mechanism: with the gated `Dictionary` authorities, `Count`/`Capacity`/`SessionCount` are unchanged and need no maintained counter. The rule is recorded for any future concurrent-index attempt |
| D15 | the bucket's reach | `UdpAssociationPool` retirement is lease-based (`UdpAssociationPool.cs:75`, `UdpControlAssociation.cs:175-181`) and the only `LastActivityUtc` consumer is test-only (`UdpAssociations.cs:138`) | the never-early claim is scoped to the legs that consume activity in production; the association stamp may be up to 500 ms older than true activity |
