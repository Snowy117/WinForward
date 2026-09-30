# Implementation notes — F3 expiry sweeps: bounded pause, zero allocation under locks

Code-grounded survey taken at planning time (`HEAD` of the working tree; anchors verified by reading the
files named below, not from the research document). Where the PRD, the research, or the benchmark
README disagree with the code, the code is quoted and the discrepancy is recorded in §9.

Method: `rg`/read over `src/`, `tests/`, `benchmarks/`, the archived research
(`.trellis/tasks/archive/2026-09/09-29-tcp-udp-path-structural-perf/research.md` §F3, §A4, §A4.5),
the recorded baseline (`benchmarks/results/2026-09-29-benchmark-coverage/`, `sweep-pause.jsonl` +
`README.md`) and the specs (`hot-path.md`, `tcp-local-redirect.md`, `udp-relay.md`,
`quality-guidelines.md`). No product build, test suite, format, inspector, or benchmark run was
executed for this note; every measurement quoted is a recorded artifact.

---

## 1. What actually sweeps, and when

`IdleExpirySweeper` (`src/WinForward.Runtime/IdleExpirySweeper.cs`) is the single production driver.
One `PeriodicTimer` (`:89`) fires at `_udpSweepInterval`, derived as
`min(mainInterval, max(5 s, relayIdleTimeout / 2))` (`:58`, `:70-76`). Production constructs it with
defaults — `new IdleExpirySweeper(dispatcher, tcpCoordinator, udpCoordinator, relayIdleTimeout:
configuration.UdpSessionIdleTimeout, logger: logger)`
(`src/WinForward.Cli/DurableCaptureBundle.cs:249`), `DefaultUdpSessionIdleTimeout = 30 s`
(`src/WinForward.Configuration/ConfigurationModels.Udp.cs:72`), main interval 1 min (`:54`).
So the **tick period is 15 s** at the shipped defaults, and the main-leg group (TCP redirects, then
the flow table) runs inside the tick only when `now - lastMainSweepUtc >= 1 min` (`:103`), i.e. every
~60 s. The UDP coordinator leg rides **every** tick (`:124`, `:162-163`).

| # | Leg | Entry point | Cadence (defaults) | Production caller |
|---|---|---|---|---|
| 1 | Flow table | `FlowDispatcher.RemoveExpiredFlows` (`src/WinForward.Runtime/FlowDispatcher.cs:132`) → `FlowTable.RemoveExpired` (`src/WinForward.Core/FlowTable.cs:90`) | ~60 s | `IdleExpirySweeper.SweepMainLegsAsync:156` |
| 2 | TCP redirect sessions + tombstones | `TcpProxyCoordinator.RemoveExpiredAsync` (`src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:456`) → `TcpRedirectSessionStore.RemoveExpiredAsync` (`TcpRedirectSessionStore.cs:101`) → `TcpRedirectTombstoneTable.RemoveExpired` (`TcpRedirectTombstoneTable.cs:92`) | ~60 s | `SweepMainLegsAsync:154` |
| 3 | UDP sessions + cooldowns | `UdpProxyCoordinator.RemoveExpiredAsync` (`src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs:341`) | 15 s | `SweepUdpLegAsync:163` |
| 4 | UDP associations (pool) | `UdpAssociationPool.SweepIdleAssociationsAsync` (`src/WinForward.Runtime/UdpProxy/UdpAssociationPool.cs:214`) | 5 s (`s_maintenanceInterval:77`, own `MaintainAsync:266-293`) | pool maintenance child |
| — | TCP redirect *table* | `TcpRedirectTable.RemoveExpired` (`TcpRedirectTable.cs:319`) | **never** — no caller in `src/` | tests only |
| — | UDP association *table* | `UdpAssociationTable.RemoveExpired` (`UdpAssociations.cs:126`) | **never** — no caller in `src/` | tests only |
| — | Pending-SYN index | `TcpPendingSynSetup.RemoveExpired` (`TcpPendingSynSetup.cs:225`) | ~60 s (rides leg 2) | `TcpProxyCoordinator.cs:457` |
| — | Setup cooldowns | `UdpSetupCooldownTable.PruneExpired` (`UdpSetupCooldownTable.cs:62`) | 15 s (rides leg 3) | `UdpProxyCoordinator.cs:346` |

Legs 1–4 plus the two "rides" sites are the real subject. Legs 5/6 (`TcpRedirectTable.RemoveExpired`,
`UdpAssociationTable.RemoveExpired`) are public API with test coverage and no production caller: a
`rg` for `RemoveExpired(` over `src/` returns only definitions plus the teardown paths; the only
invocations of both are in `tests/WinForward.Core.Tests/TcpProxyCoordinatorConcurrencyTests.cs:203`,
`TcpReversePrefilterTests.cs:62` and `CoreFlowStructuresTests.cs:197`.

`TcpPendingSynSetup.RemoveExpired` and `UdpSetupCooldownTable.PruneExpired` already allocate only when
they find something (`(expired ??= []).Add(...)`, `TcpPendingSynSetup.cs:232`, `255`;
`UdpSetupCooldownTable.cs:70`), so a no-op tick at those two sites is already allocation-free.

---

## 2. Site 1 — `FlowTable.RemoveExpired` (the pause-critical site)

`src/WinForward.Core/FlowTable.cs`.

State: `_states` (`Dictionary<FlowKey, FlowState>`, pre-sized to `capacity`, `:25`) is the authority;
`_transportIndex` (`Dictionary<TransportTuple, FlowState>`, pre-sized to `2 × capacity`, `:26`) is the
orientation-agnostic alias index (two entries per state, `:150-162`); `_freeStates`+`_freeStateCount`
is the state pool (`:10`, `:120-136`); `_gate` is one global `Lock` (`:11`) held by *every* entry —
`TryResolve` (`:49`), `TryClaimResolved` (`:63`), `Count` (`:36`), `RemoveExpired` (`:92`).

Current algorithm (`:90-118`), all under `_gate`:

```
_expiredScratch.Clear();                                  // :96  reused List<FlowKey>
foreach (var pair in _states)                             // :97  full enumeration
    if (now - pair.Value.LastActivityUtc < idleTimeout) continue;   // :99  per-entry DateTimeOffset sub
    if (isHeld is not null && isHeld(pair.Key)) continue; // :100  <-- predicate UNDER the gate
    _expiredScratch.Add(pair.Key);
foreach (var key in _expiredScratch) { _states.Remove(key, out var s); RemoveFromTransportIndex(s); ReturnState(s); }  // :106-113
```

Measured cost of one such call at 65,536 expired states: mean 26.3 ms, max 86.8 ms, 0 B allocated
(`sweep-pause.jsonl`, run 2). The pause it imposes on a concurrent `TryResolve` is **40.2 ms max**.
65,536 removals do four dictionary operations each (`_states.Remove`, two `_transportIndex.Remove`s,
plus `ReturnState`'s `Reset` + array store); at ~350–380 ns per removal that is ~23–25 ms of the 26 ms,
leaving the scan at a few milliseconds — but the split is not measured anywhere, and the design must not
assume the scan is cheap: it bounds the scan by chunk size instead (§8/F3.2, `design.md` §2).

**The nested lock edge is real and is the second half of the pause**: the sweeper passes
`_tcp.HoldsFlow` (`IdleExpirySweeper.cs:155`), which is
`_store.Holds(key) || _store.Tombstones.TryHit(key, _timeProvider.GetUtcNow())`
(`TcpProxyCoordinator.cs:468-469`) — store gate, then tombstone gate, taken **inside** the flow table's
`_gate`. The documented repo-wide order is store → redirect table → tombstone
(`TcpRedirectSessionStore.cs:25-27`); the flow sweep makes the Core flow-table gate an outer frame of
that chain, so a warm `TryResolve` waits behind store-lock contention as well as behind the scan.
A separate global lock *cycle* does not exist today (no path takes `FlowTable._gate` while holding the
store gate), so this is a latency defect, not a deadlock.

Zero allocation: this site **already allocates nothing per sweep** (`sweepAllocatedBytesPerSweep: 0`
over 8 samples, both runs), because the scratch list is reused; the first sweep after startup grows
the scratch to hold every expired key (a `FlowKey` is ~96 B → ≈6 MB at 65,536 expired) and that
capacity is retained for the process lifetime. `RemoveExpired` returns each state to `_freeStates`
(`:132-136`), so claims do not allocate either (`flowTable.claim` = 192.02 B per *new* flow on an
empty pool, unchanged across runs).

Scan/iteration cost caveat to carry into the design: the enumeration walks the dictionary's internal
entry array, whose length is a high-water mark of ever-used slots (a removed slot is freed and reused
by a later claim; `Count` subtracts free slots). The design must therefore not assume the scan is
proportional to the live population — the benchmark's 65,536 is both the live count and the
high-water count, so it cannot distinguish the two.

## 3. Site 2 — `TcpRedirectTable.RemoveExpired` (`TcpRedirectTable.cs:319-335`)

```
lock (_gate) {
  var expired = _byOriginal.Values.Where(v => now - v.LastActivityUtc >= idleTimeout).Distinct().ToArray();  // :323
  foreach (var a in expired) { _byOriginal.Remove(...); _byTranslatedListener.Remove(...); _byReverse.Remove(...);
                               RemoveAddressPairUnderGate(a); a.ReleaseOriginalSynTemplate();
                               Interlocked.Decrement(ref _candidatePorts[a.TranslatedListenerTuple.Port]); }   // :326-331
}
```

Per sweep: a `Where` iterator + a `Distinct` set + iterator + a `ToArray` — several allocations, all
under `_gate`. `Distinct()` is provably redundant: `_byOriginal` maps a distinct `FlowKey` to a
distinct association (`_byOriginal.Add` at `:217` is the only insert; `TryRemove:301` and this sweep are
the only removals), and the same instance is never inserted under two `_byOriginal` keys, so the value
sequence has no duplicates. The table is not on any production sweep path (§1), but its gate *is* on
the packet path (`TryResolveByReverse:239`, `TryResolveByOriginal:266`, `TryResolveByAddressPair:276`,
`IsReverseCandidate:253`), so its pause shape is the same class of defect at 1/4 the scale
(`_capacity` default 16,384, `:173`).

## 4. Site 3 — `TcpRedirectSessionStore.RemoveExpiredAsync` (`TcpRedirectSessionStore.cs:101-124`)

This is the **live** TCP sweep the PRD does not list. Under `_gate` (`:33`):

```
expired = [.. _sessions.Values
    .Where(s => s.Association.Phase == RelayPhase.Redirecting && now - s.Association.LastActivityUtc >= idleTimeout)
    .Select(RetireSessionUnderGate)];                       // :108-110
foreach (var retired in expired) await ReleaseRetiredAsync(retired);   // :113-116  (outside the gate)
Tombstones.RemoveExpired(now);                              // :122  rides the same tick
```

The collection expression allocates a builder + `RetiredSession` record (`:11`) per retired session,
the `Where` closure and the `Select` method-group delegate, and every `RetireSessionUnderGate`
(`:178-191`: dictionary remove, `Phase = Closing`, `Retire()`, table `TryRemove` → redirect-table gate
→ tombstone gate) runs **inside** the store gate. `TcpRedirectTombstoneTable.RemoveExpired`
(`TcpRedirectTombstoneTable.cs:92-101`) allocates `_byForward.Values.Where(...).ToArray()` under its
own gate on every tick, empty or not.

The caller also allocates per tick: `TcpProxyCoordinator.RemoveExpiredAsync` passes the closure
`() => _pendingSyn.RemoveExpired(now)` (`TcpProxyCoordinator.cs:457`) — a fresh display class +
delegate every call, from a method group that could be hoisted to a field. The same tick allocates a
second delegate in the sweeper itself: `Func<FlowKey,bool>? isHeld = _tcp is null ? null : _tcp.HoldsFlow`
(`IdleExpirySweeper.cs:155`) is an instance-method group conversion, so a new `Func<FlowKey,bool>` is
built on every main-leg tick; it is hoistable to a readonly field in the sweeper's constructor.

A tick that retires nothing still allocates here (builder + iterators + the empty tombstone array);
a tick that retires something additionally allocates one `RetiredSession` per session and whatever
the async disposal needs. The former is the gate-able invariant; the latter is inherent (disposal is
asynchronous and closes real sockets/listeners).

## 5. Site 4 — `UdpProxyCoordinator.RemoveExpiredAsync` (`UdpProxyCoordinator.cs:341-368`)

```
(UdpSessionSlot, UdpProxySession)[] idle;
lock (_gate) {                                              // _gate: :23, also taken by the warm send path
    _cooldowns.PruneExpired(now);                           // :346  lazily allocating, 0 B when quiet
    idle = [.. _sessions.Values
        .Where(slot => slot.Session is { } s && now - s.LastActivityUtc >= idleTimeout)
        .Select(slot => (slot, slot.Session!))];            // :347-349  array + closure + iterators
}
if (idle.Length > 0 && _beforeExpiryRecheck is not null) await _beforeExpiryRecheck();   // :352
foreach (var (slot, session) in idle) { if (!session.TryBeginExpiry(now, idleTimeout)) continue; ... }  // :355-365
```

The scan/collection hold includes the closure/iterator/array allocations (`_sessions` is the live
session map, `preSeed`-hinted at `:65`). The teardown loop already re-verifies each candidate outside
the gate through `UdpProxySession.TryBeginExpiry` (`UdpProxySession.cs:194-198`), so the chunked form
introduced below keeps that check exactly. The warm datagram path takes this same `_gate`
(`UdpProxyCoordinator.Send.cs:30` and the send admission), which is why the gate hold matters here
even though the cadence is 15 s: `udp-ready-path-contention.md` measured 350 → 500 ns per datagram
from 1 → 4 workers on this gate.

## 6. Site 5 — `UdpAssociationPool.SweepIdleAssociationsAsync` (`UdpAssociationPool.cs:214-230`)

```
lock (_gate) {
  if (_scope.IsSealed) return 0;
  retired = [.. _servers.Values.SelectMany(set => set.All)
      .Where(a => a.CanRetire(now, s_idleRetireTimeout) || a.CanRetireFaulted)];   // :220-221  list + iterators under the gate
  foreach (var a in retired) if (_servers.TryGetValue(a.Server, out var set)) set.Shared.Remove(a);
}
foreach (var a in retired) await a.DisposeAsync();           // :228  outside the gate
```

`CanRetire` reads the association's own `_idleSinceTicks` / `_leaseCount` volatiles
(`UdpControlAssociation.cs:175-181`), set on last lease release (`:171`); `CanRetireFaulted` is
`:190`. `s_idleRetireTimeout` is 60 s (`:75`). Nothing here is a nested-lock predicate — the
allocation under the gate is the defect. Note the *same* maintenance tick first runs
`SampleServerCapabilities` (`:279`), which allocates its candidate list unconditionally even when no
server is on trial (`:365-367`); it is adjacent to this site, not part of the AC.

## 7. How activity is stored, touched and compared today

| Owner | Storage | Written on | Guard | Read by the sweep |
|---|---|---|---|---|
| `FlowState` (`Domain.cs:154-183`) | `DateTimeOffset LastActivityUtc { get; private set; }` (`:167`) | `Touch(DateTimeOffset)` (`:169`) from `TryResolveLocked` with `_timeProvider.GetUtcNow()` (`FlowTable.cs:142`); `Reset(...)` (`:176-182`) | flow-table `_gate` | `now - LastActivityUtc < idleTimeout` (`FlowTable.cs:99`) |
| `TcpRedirectAssociation` (`TcpRedirectTable.cs:19-147`) | `DateTimeOffset LastActivityUtc` (`:62`) | `Touch(now)` (`:146`) from `TryClaim:198`, `TryResolveByReverse:245`, `TryFind:358`, `TryResolveByAddressPair:285` | redirect-table `_gate` | same subtraction (`:323`) |
| `TcpRedirectSession` | via `Association.LastActivityUtc` | as above | store `_gate` (read) | `TcpRedirectSessionStore.cs:109` |
| `UdpProxySession` (`UdpProxySession.cs`) | `long _lastActivityTicks` (`:58`), exposed as `LastActivityUtc => new(Interlocked.Read(...))` (`:96`) | `TouchActivity` (`:410-417`): a 100 ms-rate-limited propagation `Interlocked.Exchange` (`:46`), initialised from the injected clock (`:90`) | lock-free (`Interlocked`) | `now - session.LastActivityUtc >= idleTimeout` (`UdpProxyCoordinator.cs:348`) |
| `UdpAssociation` (`UdpAssociations.cs:7-23`) | `DateTimeOffset LastActivityUtc` (`:20`) | `Touch(now)` (`:22`) from `TouchActivity` (`:410-417`) / `TryClaim` / `TryFind` | association-table `_gate` | `:130` |
| `UdpControlAssociation` | `long _idleSinceTicks` | last lease release (`UdpControlAssociation.cs:171`) | `Volatile` | `CanRetire` (`:175`) |

**The research's per-hit clock claim is correct**, with one refinement. `FlowTable.TryResolveLocked`
calls `_timeProvider.GetUtcNow()` on every hit (`FlowTable.cs:142`) under `_gate`; the recorded cost
of that read is the `ReadActivityClock` row, 40.25 / 40.74 / 40.56 ns over three `--job short` runs
(`benchmarks/results/2026-09-29-benchmark-coverage/flow-table-production-shape.md`), i.e. ~34–43 % of a
same-orientation hit (92.7–117.3 ns). The TCP coordinator makes the same per-packet call on the
reverse path (`TcpProxyCoordinator.cs:106`, `:117`, `:123`).

**Defect found while verifying the claim (matters to F3.4):** `FlowState.Reset` stamps
`LastActivityUtc = DateTimeOffset.UtcNow` (`Domain.cs:181`) — the **real** clock, not the table's
injected `_timeProvider` (`FlowTable.cs:24`). Every other activity read/write at this site uses the
injected clock. `FlowTableLookupRefreshesActivityFromInjectedClock`
(`CoreFlowStructuresTests.cs:101-118`) only passes because it overwrites the stamp with
`claimed.Touch(time.GetUtcNow() - 2 min)` (`:109`) before asserting. Any bucketed representation must
unify the two clock sources or it will inherit this inconsistency at a new type.

## 8. Do the research's F3.1–F3.4 fit this codebase?

**Invariants every option must preserve** (violating any of these turns an existing test or the
recorded benchmark red, so they are the acceptance surface of §8 and §12):

1. **Exactly-once state-pool return.** A removed `FlowState` is returned to `_freeStates` once and
   never observed again (`HotPathAllocationGateTests.FlowTableRecyclesExpiredStatesThroughItsPool`
   asserts identity; `hot-path.md` "FlowTable pooling" documents the reuse contract). Any new sweep
   bookkeeping must be provably consistent with it.
2. **One round per sweep call.** `RemoveExpired` still removes *every* state that was idle-elapsed at
   entry (the benchmark throws otherwise, `SweepPauseScenario.cs:174`), which is what preserves
   "idleTimeout plus one sweep interval".
3. **The predicate is consulted only for idle-elapsed candidates, and a hold never re-arms activity**
   (`CoreFlowStructuresTests.cs:120-140`).
4. **No caller predicate runs under any table lock** (PRD requirement 3) — today `FlowTable`'s
   `isHeld` does, and it drags the store and tombstone gates in with it.
5. **Zero managed allocation on a sweep tick** (exact gate), with the async sites' invariant stated on
   the no-op tick over a populated world (`design.md` §6.1), since a retiring tick performs asynchronous
   teardown.
6. **`Count` / `Capacity` / public API / fail-closed behaviour unchanged** (PRD requirement 5).
7. **No warm-path regression**: 0 B allocation and no added clock call, lock, or per-hit mutation
   (`HotPathAllocationGateTests.FlowTableClaimAndExpireCycleAllocatesNoManagedBytes`; the
   `ReadActivityClock` / same-orientation rows).

### F3.1 intrusive timing wheel — **reject for this task**

- It does not bound the pause on its own. All 65,536 benchmark states are stamped in the same second,
  so a wheel that "processes only the buckets it crosses" processes all 65,536 in one tick: the pause
  is bounded only by the *additional* chunking, which is the mechanism actually being chosen. The
  wheel buys O(expired) total work, not a bounded gate hold.
- `Touch` runs **under the flow-table gate on every warm resolve** (`FlowTable.cs:142`), so the
  research's "unlink/relink in O(1)" becomes lock-guarded list surgery on the packet path — and the
  later F2 task's stated direction (addendum §A4 item 5, `research.md:448`: lock-free resolve,
  `Volatile.Write(activityBucket)`) is incompatible with it. Building it here would be thrown away by
  A4.
- It requires an intrusive node in every sweepable object (`FlowState` is pooled and recycled through
  `Reset`, `ReturnState`); links would need clearing on recycle, adding a new invariant to the
  exactly-once state-pool contract (`hot-path.md` "FlowTable pooling").
- Its real payoff (per-shard wheels, pause bounded by shard count) presumes the A4 sharded table,
  which the PRD puts out of scope.

### F3.2 incremental cursor sweep — **adopt, in the form the code can support**

The research's sketch ("the table holds an enumeration cursor; each tick scans only K entries,
completing a round in N/K ticks") does not transfer literally:

1. There is no stable array to hold a cursor — the authority is a `Dictionary`, whose enumerator is
   invalidated by any concurrent claim between ticks.
2. A *partial* round per tick would break the PRD's requirement 5: with N=65,536, K=2,048 and a 60 s
   main interval a round takes 32 min, so "retired no later than `idleTimeout` plus one sweep
   interval" becomes false.

The form that satisfies both: a small **live-slot registry** (`FlowState[]`, reference-typed, so no
structural change to `FlowState`) gives a stable, resumable cursor, and the round completes **inside
one call** while the `_gate` is released every K entries. The retention guarantee is then unchanged
(the call still finishes one round over the states present at entry) and the pause is bounded by K
entries' work instead of by N. See `design.md` §2.

### F3.3 zero allocation under any table lock — **adopt, at all sites**

`FlowTable` already satisfies it (reused scratch) and its gate exists (§10). The other sites do not
(§3–§6), and F3.3 is exactly the fix: reuse a scratch owned by the sweep, never build a retirement
set with LINQ inside the gate. For the async sites the achievable exact-zero tick is the *no-op* tick
over a *populated* world (a tick that actually retires sessions cannot be byte-zero — it awaits socket
disposal and allocates per retirement); that is also the tick that repeats every 15 s / 60 s in
production, and the before-code fails it. See `design.md` §6.

### F3.4 bucketed activity time — **defer to F2, adopt its sweep-side half now**

Removing "the per-hit clock call" requires the bucket to come from somewhere cheaper than a clock
read. The only such source is a bucket carried per pump iteration (addendum §A4 item 5, `research.md:448`:
"the bucket passed down from the pump iteration"). Today there is no such value anywhere on the
dispatch path: `FlowDispatcher.DispatchAsync` calls `_flows.TryResolve(packet.Context.Key, out existing)`
(`FlowDispatcher.cs:163`, `:203`) with no timestamp, and `CapturedFlowPacket`/`FlowContext`
(`FlowDispatcher.cs:34-51`, `Domain.cs:145-152`) carry none. Plumbing one is a hot-path packet-shape
change — F2/A4's own work, and exactly what the PRD puts out of scope.

Two things F3 *can* take from F3.4 without touching the packet shape, and should:

- compare an **integer cutoff computed once per call** (`cutoffTicks = now.UtcTicks -
  idleTimeout.Ticks`, then `state.LastActivityUtc.UtcTicks < cutoffTicks`) instead of a
  `DateTimeOffset` subtraction per entry — identical semantics, cheaper per entry, and the exact
  comparison shape a bucket needs;
- record the representation contract F2 must land (§ below), including the `Reset` clock defect.

Storage stays `DateTimeOffset LastActivityUtc` in F3: it is public API read by tests
(`CoreFlowStructuresTests.cs:95`, `:115`, `:134`; `HotPathAllocationGateTests.cs:441`, `:471`;
`TcpProxyCoordinatorRewriteTests.cs:31`), and `Touch(DateTimeOffset)` is called directly by tests
(`CoreFlowStructuresTests.cs:91`, `:109`, `:128`; `HotPathAllocationGateTests.cs:441`, `:451`). The
win F3.4 promises is the *clock call*, not the field type; changing the field without the bucket
plumbing would churn ~12 assertions and buy nothing.

### The F2 representation contract (as adopted by F3)

1. **Activity is a monotonic integer stamp**, compared against a **cutoff computed once per sweep
   tick**; no sweep entry does per-entry `DateTimeOffset` arithmetic. (F3 lands the cutoff half —
   `design.md` §2.2.)
2. **Storage becomes a `uint`/`long` bucket written by one atomic store** from the warm path, so a
   resolve can touch without the table lock (the current 16-byte `DateTimeOffset` write is not atomic
   and is only safe because `_gate` covers it — `touch` is what keeps F2's lock-free resolve from
   being possible today).
3. **The bucket is supplied by the pump iteration**, not read per resolve: one bucket per batch/queue
   iteration carried on `CapturedFlowPacket`/`FlowContext` down to `FlowTable.TryResolve` /
   `TryClaimResolved` and to the TCP coordinator's `Touch` sites
   (`TcpProxyCoordinator.cs:106`, `:117`, `:123`, `:436`); `TimeProvider.GetUtcNow()` must not appear
   on a per-packet hit path afterwards.
4. **Granularity** ≥ 1 s and ≤ shortest accepted idle timeout / 8 (≥ 8 buckets per retention window),
   so retirement quantization stays inside the "idleTimeout + one sweep interval" envelope.
5. **Other time consumers keep their own representation**: tombstones
   (`TcpRedirectTombstoneTable`, `DateTimeOffset` expiry), setup cooldowns, pending-SYN TTL and the
   UDP session's `long` ticks stay as they are; the bucket is for flow/association idle expiry only.
6. **One clock source per table**: `FlowState.Reset` (`Domain.cs:181`) must switch to the table's
   `TimeProvider` before or with the bucket change.
7. **The sweep keeps its properties**: cutoff per tick, integer compare per entry, zero allocation,
   no caller predicate under any table lock, and a bounded gate hold. F2 may replace the scan
   mechanism (per-shard) but not these.

## 9. Recorded discrepancies (code wins)

D1–D9 were folded into the revised PRD during review; the table is the audit trail behind its `Notes`
corrections. D10–D12 were found *in* the first planning draft by that same review.

| # | Source claim | Code reality | Effect / status |
|---|---|---|---|
| D1 | PRD table paths `src/WinForward.Runtime/Udp/UdpProxyCoordinator.cs`, `.../Udp/UdpAssociationPool.cs` | real paths are `src/WinForward.Runtime/UdpProxy/...` (the PRD's *line numbers* 341-368 / 214-230 are exact) | corrected in the revised PRD; use the real paths |
| D2 | PRD "the four sweep sites (FlowTable, TcpRedirectTable, UdpProxyCoordinator, UdpAssociationPool) allocate their retirement sets under their gates" | `FlowTable` is the one site that **does not** allocate (0 B/sweep, both recorded runs) — `benchmarks/results/2026-09-29-benchmark-coverage/README.md` already corrects this | corrected: site 1 is the pause target, the other sites are the allocation targets |
| D3 | PRD's site list omits `TcpRedirectSessionStore.RemoveExpiredAsync` and includes `TcpRedirectTable.RemoveExpired` | the session store **is** the production TCP sweep; the redirect table's `RemoveExpired` has no caller in `src/` | corrected: the PRD now numbers six sites and names files (`design.md` §1) |
| D4 | Research/PRD "the UDP leg runs every max(5 s, idleTimeout/2)" | `DeriveUdpSweepInterval` = **`min(mainInterval, max(5 s, idleTimeout/2))`** (`IdleExpirySweeper.cs:70-76`); at the defaults (relay idle 30 s, main 1 min) the tick is 15 s, and a config with a >120 s UDP idle timeout is capped at the main interval | corrected in the revised PRD; cadence reasoning must use the min() |
| D5 | Research F3.2 "the table holds an enumeration cursor" | `Dictionary`-backed table: no stable cursor exists, and a partial round per tick would violate the retention guarantee | resolved in `design.md` §2: the cursor lives over a live-slot registry and the round finishes inside one call |
| D6 | PRD requirement 2 "zero allocation on a sweep tick" at every site | a tick that retires sessions cannot be byte-zero (async disposal); the exact-zero invariant is the **no-op tick over a populated world**, which the before-code already fails | corrected: PRD requirement 2 names the gated tick per site; shapes in `design.md` §6.1 |
| D7 | Research §F3 assumes `LastActivityUtc` is the only activity store | `UdpProxySession` already uses `long` ticks + `Interlocked`/`Volatile` (`UdpProxySession.cs:58`, `:96`, `:410-417`); `UdpControlAssociation` uses `long _idleSinceTicks` | the bucket conversion in F2 is a two-of-three-site job, not a uniform rewrite |
| D8 | `sweep-pause.jsonl` metadata row | records `"flows": 64` (the *option*) while the run's parameters row and behaviour are 65,536 (`SweepPauseScenario.cs:49` clamps to `MinimumFlows`) | quote the parameters row, not the metadata row |
| D9 | `FlowState.Reset` vs the injected clock | `Reset` stamps `DateTimeOffset.UtcNow` (`Domain.cs:181`), bypassing `_timeProvider` | carried into the revised PRD as a known defect for F2; contract item 6 above |
| D10 | First-draft design: "the raw `maxPauseMs` series proves the win" | **unattributable**: the scenario's own refill makes 65,536 individual `TryClaimResolved` calls per round on the same `_gate` (`SweepPauseScenario.cs:165`), so ≥96 % of the recorded 21,252 `pausesOver500us` (≤4 observers × 141 sweeps ≈ 564 can be sweep-sourced) are refill contention this task does not touch | fixed in two stages: the phase-scoped `maxSweepWindowPauseMs` metric landed first (instrumentation before the before-artifact), and the measurement then showed even that window is host-dominated (§11.1/D13), so acceptance moved to the countable work-per-hold property (D14) |
| D11 | First-draft design: a `Lock` sweep gate at the async sites | a `Lock` must not be held across the `await`s in the disposal tails, and `TcpProxyCoordinator.RemoveExpiredAsync` / `UdpProxyCoordinator.RemoveExpiredAsync` are **public**, so a second caller could clear a scratch another caller is still draining (stranded relays/listeners) | fixed: `SemaphoreSlim(1,1)` + `WaitAsync()` across the whole method, with the scratch lifetime and clear site specified per site (`design.md` §4–§6) |
| D12 | First-draft design: `ChunkEntries = 512`, "~10–80 µs per scan hold" | arithmetic wrong: 512 × 300–400 ns = 154–205 µs, and the recorded *worst* sweep is 1.32 µs/state (86.836 ms / 65,536) → ~678 µs at 512, which misses the 0.5 ms line | fixed: `ChunkEntries = 256` → ≤ ~338 µs at the worst recorded per-state cost (`design.md` §2.2) |
| D13 | First implementation pass: "the in-window max is a hold measurement" | the control run with the window armed ~120 ms and **no product call at all** still measured `maxSweepWindowPauseMs` 10.0064 ms / 13,181 overshoots; 1 observer: 63 of 5.30 M (0.0012 %); out-of-window 0.42 % vs in-window 0.14 % | fixed: timing reclassified as series/diagnostics with the control quoted beside it; §11.1 records the numbers |
| D14 | First implementation pass: "lower `ChunkEntries` to meet the 0.5 ms line" | provably a no-op in the all-expired shape (each entry is examined once and removed once regardless of `K`), and the line was unattributable anyway (D13) | fixed: acceptance is the countable work-per-hold probe (D-A), `K` is a hold-size knob only |
| D15 | First implementation pass: the per-removal round's cost | `sweepMeanMs` 26.2 → ~120 ms, ~135 k `_gate` acquisitions per round (the scenario's clock makes every chunk stop at its first entry) | fixed: D-B's batched phase 3 (~512 holds/round), guard `sweepMeanMs` ≤ 2× the 26.2 ms before-series |
| D16 | D-B as first sketched: "swap-removing without moving the cursor" | loses states — simulated N=65,536 all idle-elapsed, K=256 → `removed = 32,768`, 32,768 expired states left live, round ends at `cursor == liveCount == 32,768` | fixed: the phase-3 cursor rewind is part of the algorithm (`design.md` §2.2), pinned by the all-idle-elapsed round test and the scenario tripwire |

## 10. Does a sweep zero-allocation gate already exist, and what does it cover?

Yes — one site, one shape.

- `tests/WinForward.Core.Tests/SweepAllocationGateTests.cs:24-58`
  (`FlowTableSweepAllocatesNoManagedBytes`): 4,096 seeded flows, capacity +16, `idleTimeout =
  TimeSpan.Zero`, `now = UtcNow + 1 s`, **no `isHeld` predicate**, one measured call after a
  ≤8-iteration stabilization loop that requires an exactly-zero per-thread delta before the measured
  window opens (`:35-47`), then `Assert.Equal(0, allocated)` with the managed thread id pinned
  (`:50-57`). It asserts nothing about gate-held duration, nothing about a predicate being invoked,
  and nothing about a partially-expired table.
- Its discriminating companion, `AllocationProbeSeesAKnownAllocation` (`:64-74`), proves the probe can
  see an allocation; the benchmark README records that injecting 16 B into the sweep turns the gate red
  with `Expected: 0, Actual: 40`.
- `tests/WinForward.Core.Tests/HotPathAllocationGateTests.cs:415-461`
  (`FlowTableClaimAndExpireCycleAllocatesNoManagedBytes`): 64-capacity table, 256 measured
  claim + `Touch` + 1-state-expiry cycles inside a **single** 0 B window with no stabilization loop —
  this one does cover `Touch` and a 1-entry sweep, and is the strongest existing constraint on this
  site.
- Nothing gates `TcpRedirectTable.RemoveExpired`, `TcpRedirectSessionStore.RemoveExpiredAsync` +
  `TcpRedirectTombstoneTable.RemoveExpired`, `UdpProxyCoordinator.RemoveExpiredAsync` or
  `UdpAssociationPool.SweepIdleAssociationsAsync`; the benchmark-coverage design (R3b) and its README
  both record those as F3's targets whose "gates land with the fix, because a gate added now would be
  red on an unmodified tree". Their **before bytes are therefore not recorded anywhere** — the exact
  red numbers must be captured in each site's own step (gates land with their fix; see `implement.md`).

## 11. Reproducing the before-numbers

Recorded artifact: `benchmarks/results/2026-09-29-benchmark-coverage/sweep-pause.jsonl`
(host: NixOS 26.11 (Zokor), .NET 10.0.12, X64; two `--quick` runs, 15 s observation each, 4 observers).

```bash
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario sweep --quick --tcp-concurrency 4 \
  --output benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/sweep-pause-before.jsonl
```

| Metric (run 2 / run 1) | Value |
|---|---|
| `sweeps` / `removedPerSweep` | 141 / 146 ; 65,536 both |
| `sweepMeanMs` / `sweepMaxMs` | 26.261 / 86.836 ; ~25.1 / ~87.1 |
| `maxPauseMs` (report-only; see attribution below) | 40.2343 ; 39.5 |
| `pausesOver100us` / `500us` / `1ms` / `5ms` | 30,457 / 21,252 / 13,949 / 1,311 ; 41,704 / 25,284 / 14,022 / 828 |
| `sweepAllocatedBytesPerSweep` | 0 ; 0 |
| `resolves` | 3,494,039 ; (not quoted) |
| `flowTable.claim` ns / bytes | 1,993.1 / 192.02 B ; 2,339.7 / 192.02 B |

**Attribution — the raw series cannot carry this finding.** Every round first refills the table with
65,536 **individual** `TryClaimResolved` calls (`SweepPauseScenario.cs:165`), each of which takes the
same `_gate` the observers use. Those calls are the dominant source of observer-visible pauses: at most
4 observers × 141 sweeps ≈ **564** resolves can be blocked by a sweep's own hold, against **21,252**
recorded pauses over 500 µs — ≥96 % are refill contention this task does not touch. The refill also
explains the shape of the numbers: 141 rounds in 15 s means ~106 ms per round of which the sweep itself
is only ~26 ms (`flowTable.claim` ≈ 2 µs × 65,536 ≈ 130 ms of claiming per round), and the observers
managed only 3.49 M resolves in 15 s (≈ 17 µs per resolve across 4 threads) because they were mostly
queued behind it.

Consequences for the plan (implemented in `design.md` §8–§9 and `implement.md` Step 1):

1. the scenario gains a **phase-scoped metric** — a volatile flag armed around the `RemoveExpired` call
   (`:172`), with observers aggregating only the resolves that started inside the window into
   `maxSweepWindowPauseMs` and in-window counts, plus `sweepWindowResolves` so a vacuous window is
   visible;
2. the **instrumentation lands before the before-artifact is captured**, so both artifacts come from the
   same scenario and are comparable;
3. raw `maxPauseMs` / `pausesOver500us` stay report-only and are quoted **normalized per sweep**
   (e.g. pauses per sweep round) with this attribution stated beside them.

The probe's remaining shape bounds what the after-run may claim (`SweepPauseScenario.cs`): the sweeper
refills 65,536 already-expired states and **throws unless one `RemoveExpired` call removes all
65,536** (`:174`), the observers resolve 4,096 live TCP keys whose hold predicate is
`static key => key.Protocol == TransportProtocol.Tcp` (`:172`), and the sweep's notion of *now* is 31 s
ahead of the wall clock, so every observer is a "held, idle-elapsed" candidate on every round. Any
design that returns a partial round from one call breaks this scenario — the chosen design does not.

F3.4's baseline (`ReadActivityClock`, the per-hit clock the bucket would remove):

```bash
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*FlowTableProductionShape*' --job short
```

40.25 / 40.74 / 40.56 ns over three runs (`flow-table-production-shape.md`), against a
same-orientation hit of 92.7–117.3 ns.

### 11.1 Measured outcome of the first implementation pass (2026-09-30) — the calibration record

Instrumented scenario, 4 observers, runs 1/2/3, measured with the registry + the **per-removal** chunked
round in place (i.e. the state before D-B):

| | before | after |
|---|---|---|
| `sweepWindowResolves` (resolves completed inside sweep windows) | 10,446 / 8,657 / 6,469 | **11.6 M / 14.2 M / 12.1 M** |
| `maxSweepWindowPauseMs` | 92.57 / 36.44 / 44.79 | 4.71 / 6.90 / 7.56 — **misses 0.5 ms** |
| `maxPauseMs` | 92.57 / 56.68 / 44.79 | 9.28 / 8.70 / 10.39 |
| `pausesOver500us` | 25,625 / 24,623 / 23,440 | 30,647 / 28,775 / 29,990 |
| `sweeps` / `sweepMeanMs` | 139/26.2, 147/25.6, 139/26.5 | 73/119.9, 69/129.2, 79/112.1 |
| `removedPerSweep` / `sweepAllocatedBytesPerSweep` | 65,536 / 0 | 65,536 / 0 |

Three controls, all recorded in the task's artifact README:

1. **1 observer, same code:** 63 in-window overshoots of 5.30 M resolves (0.0012 %), sweep 54 ms.
2. **Window armed ~120 ms with no product call at all** (4 observers): still `maxSweepWindowPauseMs`
   10.0064 ms and 13,181 in-window >0.5 ms — the same order with **zero sweep work**.
3. Out-of-window (refill, no sweep hold) exceeds 0.5 ms **0.42 %** of the time vs **0.14 %** in-window.

Readings:

- **The in-window maximum measures host scheduling and lock queueing, not hold length.** Controls 1–3
  show the same order of overshoot on a run that performs no sweep work at all, so a 0.5 ms wall-clock
  line is not attainable on this host. This supersedes §11/D10's framing: the phase-scoped metric fixed
  *attribution* (no longer refill contention) but not *attributability to the hold*.
- **The user-visible win is real and large**: `sweepWindowResolves` 6.5–10.4 k → 11.6–14.2 M per 15 s
  window — the warm path stops starving behind the sweep. That is the D-C claim, and it is a series.
- **The per-removal draft costs total sweep time**: `sweepMeanMs` 26.2 → ~120 ms (sweeps per window
  139/147/139 → 73/69/79), because the scenario's clock makes every entry idle-elapsed, so each chunk
  stops at its first entry and the design makes ~135 k short `_gate` acquisitions per round. D-B's
  batched phase 3 (~512 holds/round) is the fix, guarded at ≤ 2× the 26.2 ms before-series.
- **`ChunkEntries` cannot fix any of this**: in the all-expired shape every entry is examined once and
  removed once regardless of `K`, so lowering it only shrinks each hold; and the reading it was meant to
  fix was host scheduling (control 2).
- The acceptance property therefore moved to **counts** (D-A): no `_gate` hold inside
  `FlowTable.RemoveExpired` performs more than `SweepChunkEntries` examinations or removals, proven by
  the `HoldProbe` sink in Release (`design.md` §2.2/§6.1/§8).

## 12. Dead ends (recorded against re-exploration)

- **Timing wheel on the current table** — see §8/F3.1: does not bound the mass-expiry pause by itself,
  puts list surgery under the warm gate, and is superseded by A4.
- **Partial-round cursor across ticks** (the research's literal F3.2) — violates the retention
  guarantee at any workable K (§8/F3.2), and cannot be resumed over a `Dictionary` anyway.
- **A reader-writer or lock-free table gate for the sweep** — every warm resolve *writes* activity, so
  readers are writers until F3.4/F2 land; a `ReaderWriterLockSlim` would not take the scan off the
  writer path.
- **Copying the key set to a per-sweep snapshot** — the same instability problem as the cursor (the
  snapshot itself needs a full enumeration under the gate, which is the pause being removed).
- **Second-chance / referenced-bit ageing instead of exact timestamps** — would make expiry
  quantized-to-rounds and break the exact boundary assertions (`CoreFlowStructuresTests.cs:96-97`,
  `:116-117`; `HotPathAllocationGateTests.cs:452`) that are this table's semantics contract.
- **`Distinct()` in `TcpRedirectTable.RemoveExpired`** — provably a no-op (§3); deleting it is part of
  the fix, not an optimization to be reverted.
- **Shrinking the flow table's capacity or raising the sweep interval** — trades the correction's
  meaning (retention) for the metric; not attempted.
- **The in-sweep-window wall-clock maximum as the acceptance figure** — measured dead (§11.1): a control
  run with the window armed and no product call at all reproduces the same order of overshoot
  (10.0064 ms), so the metric cannot be attributed to the hold. Acceptance lives in the countable
  work-per-hold property (D-A).
- **Batched phase 3 without the cursor rewind** (the D-B sketch as first written) — loses states:
  N=65,536 all idle-elapsed, K=256, simulated exactly → `removed = 32,768` with 32,768 expired states
  still live, because the round ends at `cursor == liveCount == 32,768` while each removal moved an
  unexamined tail element below the cursor. The rewind
  (`from != slot && from >= cursor → cursor = min(cursor, slot)`) is part of the algorithm, not a
  refinement (`design.md` §2.2).
- **The raw `maxPauseMs` / `pausesOver500us` series as acceptance evidence** — the scenario's own refill
  dominates it (§11/D10), so a green after-number would prove nothing about the sweep; the phase-scoped
  `maxSweepWindowPauseMs` is the metric, and the raw series is quoted normalized with the attribution.
