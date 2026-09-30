# Implementation notes — F2 warm-path lock chain

Code-grounded reconnaissance for task `09-30-warm-path-lock-chain` (finding F2 of
`archive/2026-09/09-29-tcp-udp-path-structural-perf/research.md` §F2, addendum §A2/§A4). Every claim below
is anchored to the tree at `c6f58b7` (post-F3: `09-30-expiry-sweep-bounded-pause` archived, `FlowTable`
carries `_liveStates`, the chunked minimal-granularity round, `SweepHoldProbe` and `_sweepGate`).
**Code wins over the research wherever they disagree**; §9 records every disagreement found.

Read order for a reviewer: §1–§2 are the two sequences the task exists to shorten; §3–§6 are the four
sites; §7 is the measurement surface; §8 is the hazard list per candidate mechanism; §9 the discrepancies;
§10 the dead ends; §11 the reproduce-before commands and their recorded numbers.

---

## 1. The warm TCP redirect packet, exactly

A mid-flow data packet on an established proxied TCP flow, trace logging off, captured on an adapter
pump thread. Sequence with the file:line of every acquisition:

| # | Step | Site | Lock | Hash probes | Clock |
|---|---|---|---|---|---|
| 1 | `WantsPacket` — TCP ∧ `_candidatePorts[srcPort] != 0` | `FlowDispatcher.cs:160` → `TcpProxyCoordinator.cs:335-339` → `TcpRedirectTable.cs:270` | none (`Volatile.Read`) | 0 | 0 |
| 2 | **self-traffic** `IsOwned` (full: exact pair + wildcard pair) | `FlowDispatcher.cs:162` → `SelfTrafficRegistry.cs:24-33` | registry `_gate` | ≤4 (`_entries[key]`, `_entries[reverse]`, `_wildcards[key]`, `_wildcards[reverse]`) | 0 |
| 3 | **flow resolve** `TryResolve` | `FlowDispatcher.cs:163` → `FlowTable.cs:131-137` → `:329-339` | flow-table `_gate` | 1–2 (`_states[key]`, then `_transportIndex[tuple]`) | 1 per hit (`_timeProvider.GetUtcNow()`, `:333`) |
| 4 | proxy decision → `_servers.TryGetValue` (string key) | `FlowDispatcher.cs:173` | none | 1 | 0 |
| 5 | `_executor.ProxyAsync` → `tcpProxy.HandlePacketAsync` | `NdisPacketActionExecutor.cs` → `TcpProxyCoordinator.cs:378` | — | — | 1 (`:386`) |
| 6 | `IsReverseCandidate` (miss for a forward packet) | `TcpProxyCoordinator.cs:391` → `TcpRedirectTable.cs:259-262` | redirect `_gate` | 1 | 0 |
| 7 | SYN bit-test on the frame | `TcpProxyCoordinator.cs:389` | none | 0 | 0 |
| 8 | `TryResolveByOriginal` | `TcpProxyCoordinator.cs:408` → `TcpRedirectTable.cs:272-273` → `:389-401` | redirect `_gate` (2nd entry) | 1 | 0 |
| 9 | `ReinjectExistingFlowDataAsync` → rewrite + inject (+ `TryDeferRedirectFrame`) | `TcpProxyCoordinator.Injections.cs` | sequence tracking takes `TcpRedirectAssociation._sequenceGate` (`TcpRedirectTable.cs:115`) | 0 | 0 |

Totals for one warm forward TCP packet: **4 process-wide gate entries** (registry 1, flow table 1,
redirect table 2) + **8 dictionary probes** (registry 4 — a miss evaluates both exact and both wildcard
`ContainsKey` calls, `SelfTrafficRegistry.cs:30-31` — flow table 1 on an exact hit or 2 on a transport-alias
hit, `_servers` 1, redirect `IsReverseCandidate` 1, redirect `TryResolveByOriginal` 1) + **2 clock reads**
(flow-table `Touch`, `TcpProxyCoordinator.cs:386`). On the reverse leg (`HandleReverseIfApplicableAsync`,
`TcpProxyCoordinator.cs:348-370`) the same shape with steps 6 and 8 both probing `_byReverse`:
`IsReverseCandidate` (`:359`, gate) and then `HandleReverseAsync` → `TryResolveByReverse`
(`TcpProxyCoordinator.Injections.cs:305`, gate again) — **the same dictionary, twice, gate entered twice**,
with a third `GetUtcNow()` between them (`Injections.cs:305`).

The dispatcher's warm entry rejects nothing on the happy path, so the per-packet cost above is the whole
steady-state cost before any executor work. Measured composition of the same shape on this host
(`benchmarks/results/2026-09-29-benchmark-coverage/README.md`): a same-orientation resolve hit 92.7–117.3 ns,
a reverse-alias hit 134.8–138.4 ns, the activity clock read 40.3–40.7 ns.

## 2. The warm UDP datagram, exactly

`UdpProxyCoordinator.Send.cs:21-77`, called from `NdisPacketActionExecutor.cs:450`:

| # | Step | Site | Lock | Probes | Clock |
|---|---|---|---|---|---|
| 1 | coordinator `_gate` entry | `UdpProxyCoordinator.Send.cs:27` | `UdpProxyCoordinator._gate` (`:23`) | 0 | 0 |
| 2 | `_scope.IsSealed` | `:29` → `QuiescenceScope.cs:64` | (none; volatile) | 0 | 0 |
| 3 | `now = _timeProvider.GetUtcNow()` | `:30` | — | 0 | **1** |
| 4 | cooldown probe `_cooldowns.TryHit(flow, now)` | `:31` → `UdpSetupCooldownTable.cs:35-44` | cooldown `_gate` (`:22`) — **nested inside the coordinator gate** | 1 | 0 |
| 5 | session probe `_sessions.TryGetValue(flow, out slot)` | `:37` | (same coordinator gate) | 1 | 0 |
| 6 | `slot.Ready` | `:64` | — | 0 | 0 |
| 7 | `session.SendSpanAsync` → `lock (_activityGate)`; `_scope.Fault`, `_expiring`, `_scope.TryEnter` | `UdpProxySession.cs:139-148` | session `_activityGate` (`:55`) + `QuiescenceScope` CAS (`QuiescenceScope.cs:78-95`) | 0 | 0 |
| 8 | `_transport.SendSpanAsync` → `_sendGate.WaitAsync` → encode → `Socket.SendTo` | `Socks5UdpTransport.cs:306-359` | transport `SemaphoreSlim(1,1)` (`:194`) | 0 | 0 |
| 9 | `TouchActivity` (success path) | `UdpProxySession.cs:410-427` | `_activityGate` again (`:413`) | 0 | **1** (`:412`) |

Totals for one warm UDP datagram: **coordinator gate + cooldown gate + session gate + transport
semaphore = 4 lock entries (coordinator, cooldown, session ×2) + 1 semaphore**, 2 dictionary probes,
**2 clock reads**. `SendSpanAsync`'s
`_activityGate` is taken twice per datagram (steps 7 and 9), so the session lock is entered 2×.
`TouchActivity` additionally rate-limits the association-table propagation to 100 ms
(`s_activityPropagationInterval`, `:46`) — the propagation is not per-datagram, the lock is.

Requirement 5's "session lookup + ready check takes at most one `TryEnter`-style attempt" is a *floor*:
the lookup can be made wait-free (§6 of `design.md`), which is stronger and drops no datagram.

## 3. `SelfTrafficRegistry.IsOwned` — what it actually does, and the two halves

`SelfTrafficRegistry.cs:24-33` builds a key from the *context* (`SelfTrafficKey.From(context)`,
`:52` — protocol + local + remote endpoint, **no origin/adapter fields**) and probes four dictionaries
under one gate:

| Half | Probe | Registered by | Match shape |
|---|---|---|---|
| **exact** | `_entries[key]`, `_entries[reverse]` (`:30`) | `TcpRedirectSetup.cs:179` (the *translated* listener tuple, as `(Tcp, translatedTuple, translatedTuple)` — **after** the original flow is claimed at `:66`), `UdpSessionSetup` for the relay alias | all four tuple fields (protocol + both endpoints) |
| **wildcard** | `_wildcards[WildcardKey.From(key)]` + reverse (`:31`), keyed on protocol + local **port** + remote (`:56-58`), populated only when the registered local address is `0.0.0.0`/`::` (`:19`, `:35`) | `TcpProxyRelay.cs:36-44` — the upstream **control connection**, whose socket is bound to a wildcard local endpoint, registered *before its SYN leaves the host* (the in-tree comment says so: "the registration uses Any:port and the wildcard matcher in SelfTrafficRegistry covers the routing-chosen source IP") | protocol + local port + remote endpoint; the local **address is deliberately dropped**, so it matches whatever source IP the routing stack picked |

Entries carry a monotone generation (`:17`); `SelfTrafficToken.Dispose` removes only if the generation
still matches (`:41-45`), so a stale token cannot delete a newer registration.

**Why the halves must be split, not moved wholesale.** The registry is a **bad-news structure**: a false
negative loops the proxy into its own traffic (forbidden by research §A5), a false positive merely passes
a packet that could have been proxied. Moving the *whole* check to claim time is unsound:

- The wildcard half exists precisely to catch a **port recycled to a relay control socket**: a host flow
  to the SOCKS5 endpoint claimed on ephemeral port P leaves a `Proxy` state in the flow table; when P is
  recycled, `TcpProxyRelay` registers `(Tcp, Any:P, proxyEndpoint)` before its SYN. With the warm-entry
  check gone entirely, that SYN resolves the stale `Proxy` state on the warm path and WinForward's own
  control connection is redirected into its own proxy — recursively, and **durably**, because every warm
  hit re-touches the state so it never goes idle while traffic flows. The wildcard half therefore stays
  on the warm path, answered by a `ConcurrentDictionary` probe (registry writes are cold, so this still
  satisfies requirement 1's no-global-lock clause).
- The exact half *can* move, because a self-owned **exact** tuple never produces a state: the claim-time
  check (`FlowDispatcher.DispatchSlowAsync` → `TryHandleSelfTrafficAsync`, `:194`, `:246-252`) already runs
  before the reverse hook and before `TryClaimResolved` (`:225`) for every packet that reaches the slow
  path, so a claimed state is itself the "proven not self" record and no per-flow flag is needed.

**The delta's real bound** (the PRD note says "until that flow expires"; the code says more): every warm
hit calls `Touch`, so an already-claimed flow whose **exact** tuple is registered later keeps being
proxied **for as long as it keeps receiving packets** — the 5-minute flow idle timeout
(`IdleExpirySweeper.cs:59`) only starts after the traffic stops. Both registration sites must be named
because the ordering is not uniform: `TcpProxyRelay.cs:36-44` registers before its SYN (so its own
traffic is never claimed), while `TcpRedirectSetup.cs:179` registers the translated tuple **after** the
original flow's claim in the same pipeline (`:66`), so for that shape the only protection is the claim-time
check. Recorded in `design.md` §4/§7/§8 and in the task's discrepancy table, not silently dropped.

Requirement 2 in the amended PRD is therefore: keep a new interface member
(`IsWildcardOwned`, ≤2 lock-free probes) on the warm entry, and keep `IsOwned` (full) exactly where it
already is — one call site added, one call site changed, no new state.

## 4. How `FlowState` activity is stored and touched today, and what the bucket must change

Today (with F3 in place):

| Aspect | Code | Consequence for a lock-free read |
|---|---|---|
| Storage | `public DateTimeOffset LastActivityUtc { get; private set; }` (`Domain.cs:167`) | 16 bytes, **not** atomically writable; every read and write is safe only because `_gate` covers both |
| Write on hit | `FlowState.Touch(now)` (`Domain.cs:169`) from `TryResolveLocked` (`FlowTable.cs:333`) with `_timeProvider.GetUtcNow()` under `_gate` | the per-hit clock call is *inside* the lock; removing the lock without changing the representation is a data race |
| Write on claim | `Reset` stamps `DateTimeOffset.UtcNow` (`Domain.cs:181`) — the **real** clock, bypassing `_timeProvider` (F3 defect **D9**) | a bucket change must unify the source or the defect moves to a new type |
| Read by the sweep | `state.LastActivityUtc.UtcTicks > cutoffTicks` (`FlowTable.cs:254`) and `<= cutoffTicks` (`:283`), `cutoffTicks = now.UtcTicks - idleTimeout.Ticks` computed once per call (`:194`) | F3 already landed the integer-cutoff half; the remaining half is the field |
| Read by the registry scan | `_liveStates[cursor]` under `_gate` (`:248`) | unchanged by this task |
| Return to pool | `ReturnState` → `Reset(default, default, 0)` (`:325`), exactly-once via the swap-remove at the cursor (`:287-288`) | **the ABA source**: a pooled state can be re-`Reset` for another flow while a lock-free reader still holds the reference |

What the bucket conversion must change, per the seven-item contract F3 recorded
(`archive/2026-09/09-30-expiry-sweep-bounded-pause/research/implementation-notes.md:313-336`):

1. **Item 1** (monotonic integer stamp, cutoff once per tick) — already landed; the field becomes the
   integer.
2. **Item 2** (one atomic store from the warm path) — `long _activityBucket` written with
   `Volatile.Write`; `DateTimeOffset LastActivityUtc` becomes a derived property
   (`new DateTimeOffset(bucket * width, TimeSpan.Zero)`), so it stays readable by the existing
   assertions at a bucket-aligned value.
3. **Item 3** (bucket supplied by the pump iteration, not read per resolve) — the *mechanism changes*
   from "a field on `CapturedFlowPacket`/`FlowContext`" to **a shared `ActivityBucketClock` published
   once per pump iteration through the composition's existing per-iteration seam**
   (`NdisCapturePumpOptions.OnBatchCompleted`, `NdisCapture.cs:43-44`, wired at
   `MultiAdapterCaptureLoop.cs:34-46` and invoked once per iteration including empty polls,
   `NdisCapture.cs:290`, `:308`). Rationale in `design.md` §3: the packet-shape change touches
   `NdisCapturedPacket` (NdisApi), the classifier, `CapturedFlowPacket` and every test that builds
   one; the callback seam yields the identical property (exactly one clock read per iteration, zero per
   packet) with a much smaller diff, and `CapturePacketProcessor.OnBatchCompleted` already exists for
   exactly this class of per-iteration work (`CapturePacketProcessor.cs:35-41`).
4. **Item 4** (granularity ≥1 s and ≤ shortest accepted idle timeout / 8) — **internally inconsistent**:
   the shortest accepted idle timeout is `MinimumUdpSessionIdleSeconds = 5`
   (`ConfigurationLimits.cs:58`), whose /8 is 0.625 s. `design.md` §3.1 chooses **500 ms** and records
   the restatement ("≥8 buckets per retention window" is the binding half).
5. **Item 5** (other time consumers keep their representation) — kept for tombstones
   (`TcpRedirectTombstoneTable`), the setup cooldown table, pending-SYN TTL and the UDP setup queue
   stamps. **Extended** for `TcpRedirectAssociation` and `UdpProxySession` activity *sources* (their
   per-packet clock reads at `TcpProxyCoordinator.cs:386`, `Injections.cs:305` and
   `UdpProxySession.cs:412` are what "clock-call-free" has to mean), while their stored representation
   stays a `DateTimeOffset`/`long` written from the published bucket.
6. **Item 6** (one clock source per table) — `Reset` switches to the injected `ActivityBucketClock`;
   D9 is fixed by construction rather than re-stated.
7. **Item 7** (the sweep keeps cutoff-per-tick, integer compare, zero allocation, no caller predicate
   under a lock, bounded hold) — the F3 round, `SweepHoldProbe` counts and `_liveStates` registry are
   untouched by the resolve-path change; only the comparison operator moves one notch (`design.md` §3.3),
   and the compare reads the **internal integer bucket**, never `LastActivityUtc`.

Two further representation rules that only surfaced against the test and benchmark surface:

8. **`idleTimeout ≤ TimeSpan.Zero` must keep meaning "retire everything on this call"** — the bucket form
   `BucketOf(now − 0) > stamp` would keep a current-bucket stamp alive to the next edge (≤500 ms) and
   three call sites depend on the drain: `UdpChurnScenario.cs:234` (a loop that throws unless
   `SessionCount == 0`), `UdpSessionBudgetRun.cs:97`, `SessionSetupDecompositionBenchmarks.cs:249`
   (`Assert(removed == Sessions, "T3 must retire every session in one expiry sweep")`). Rule: a
   non-positive timeout yields `cutoffBucket = long.MaxValue`. Positive sub-bucket timeouts need no rule
   (the never-early derivation holds for any `idleTimeout > 0`), but they *do* change the retirement
   instant by up to one bucket, which is why `IdleExpirySweeperFailureTests.cs:57` (a 50 ms relay idle
   timeout whose ≥4/≥5 failing-tick assertions at `:63-69` depend on the session being idle-elapsed on
   the first ticks) must be re-based on a bucket edge rather than left bucket-phase dependent.
   **Do not "tidy"** `SweepAllocationGateTests`' `DateTimeOffset.UtcNow.AddSeconds(1)` sweep instants
   (`:78`, `:88`, `:177`, `:183`, `:196`) — 1 s is two buckets, so they stay exactly-expired — nor the
   `now − 2 min` / `now` pair in the same class.
9. **The propagation sentinel must not be `0`.** `UdpProxySession`'s association propagation uses
   `Interlocked.Read(ref _lastActivityPropagationTicks) == 0` as "never propagated" (`:423-426`). Bucket 0
   is a real bucket (any instant in `[UnixEpoch, +500 ms)`), and `UdpProxySessionTests` starts its clock at
   `UnixEpoch` (`:23`, `:38`), so a `0` sentinel would suppress the session's first propagation and break
   the documented immediate-first-propagation behaviour. Use `long.MinValue` (or an explicit flag).

## 5. The TCP reverse-candidate + resolve pair, and why the gate is entered twice

Two entry paths, both redundant:

- Warm diversion `WantsPacket` (`TcpProxyCoordinator.cs:335-339`) uses the lock-free listener-port
  refcount (`TcpRedirectTable.cs:270`, incremented in `TryClaim` under the gate at `:233`, decremented in
  `TryRemove` at `:319` and `RemoveExpired` at `:365`). A miss falls through to the flow table, and if the
  flow table also misses the slow path runs the full handler — the documented fall-through theorem
  (`hot-path.md:36-40`).
- `HandleReverseIfApplicableAsync` then does `Table.IsReverseCandidate(key.Local, key.Remote)`
  (`:359`, gate + `_byReverse` probe) and, on a hit, `HandleReverseAsync` → `TryResolveByReverse`
  (`Injections.cs:305`, gate + the *same* `_byReverse` probe). `HandlePacketAsync` has the same pair at
  `:391` / (implicitly, through `HandleReverseAsync`).
- `IsReverseCandidate` (`TcpRedirectTable.cs:259-262`) is exactly `_byReverse.ContainsKey(tuple)`;
  `TryResolveByReverse` (`:245-257`) is exactly `_byReverse.TryGetValue(tuple)` + `Touch`. The pair is a
  `ContainsKey` followed by a `TryGetValue` of the same key that could have returned the value — one
  probe is pure waste, and the second gate entry is what requirement 4 removes.

Ordering detail that constrains the fold: the tombstone grace is consulted only when the reverse index
**misses** (`TcpProxyCoordinator.cs:364-366` and `:419`). Folding `IsReverseCandidate` into
`TryResolveByReverse` preserves that (hit → reverse handling, miss → tombstone → `NotRelevant`), because
`TryResolveByReverse`'s null result is the same predicate `ContainsKey` returned.

**The fold is call-site complete or it does not exist.** `HandleReverseIfApplicableAsync` needs the probe
result to decide the tombstone branch (`:359-367`) and `HandlePacketAsync` needs it to route reverse
before the SYN/data legs (`:391`), while `HandleReverseAsync` performs the probe that actually yields the
association (`Injections.cs:305`). Deleting only `IsReverseCandidate` therefore leaves **two**
`TryResolveByReverse` probes per warm reverse packet. The fix is to resolve once at `:359`/`:391` and pass
the association into `HandleReverseAsync`, deleting `:305` — the exact fact counts *probes*, not gate
entries, so the 2→1 claim is checked on the right counter.

**Scope discipline on the other two indexes:** `_byTranslatedListener` and `_byAddressPair` stay
`Dictionary` reads under `_gate` (`:159-160`, `TryResolveByAddressPair` at `:287-296`). The address-pair
index is written by every claim (`:229`) and read only by the fragment path
(`TcpProxyCoordinator.cs:441`), which is not the warm packet path; making it concurrent would add a third
index to the coherence argument for no measured win. `TcpRedirectTable.Count` (`:182-188`) currently reads
`_byOriginal.Count` **under `_gate`** — on a `ConcurrentDictionary` that property acquires every bucket
lock, so it needs a maintained `_count` next to every `_byOriginal` mutation (`:223`, `:312`, `:350-366`),
the same rule as `FlowTable`.

## 6. The UDP ready path's lock/semaphore chain, and which links are removable

Chain: coordinator `_gate` (`UdpProxyCoordinator.Send.cs:27`) → cooldown `_gate`
(`UdpSetupCooldownTable.cs:37`) → session `_activityGate` (`UdpProxySession.cs:142`, again at `:413`) →
transport `_sendGate` `SemaphoreSlim(1,1)` (`Socks5UdpTransport.cs:194,321`).

Facts that decide the design:

- **The cooldown probe is unnecessary on the ready path.** `_cooldowns.Write` has exactly two call sites:
  `UdpProxyCoordinator.cs:434` (inside `RemoveSlotAsync`, in the same `_gate` hold that removed the slot
  at `:420`) and `Clear()` at dispose (`:243`). A flow in cooldown therefore has **no session slot** at
  the moment the write becomes visible, and the admission path re-checks the cooldown before creating a
  slot (`Send.cs:31`). So "ready session ∧ cooldown" is unreachable, and looking up the session first
  cannot change any outcome.
- **The clock read exists only to feed the cooldown probe** (`Send.cs:30`); with the probe moved to the
  cold path the window has one clock read per *datagram* → zero.
- **`_sessions.Count` is not usable as a lock-free capacity check.** `ConcurrentDictionary.Count`
  acquires every bucket lock; a separate `int` maintained in the same gate holds as every `_sessions`
  mutation keeps `Count`'s gate-consistent semantics (`SessionCount`, `UdpProxyCoordinator.cs:97-100`)
  at one volatile read. All `_sessions` mutation sites that must maintain it: the add at `Send.cs:61`,
  the removal at `UdpProxyCoordinator.cs:420`, and the dispose drain (`:225-277`, which iterates the
  snapshot and does **not** currently remove — it must, or the counter must be zeroed there).
- **`slot.Ready` / `slot.Session` are plain fields** (`UdpProxyCoordinator.cs:486-492`) written under
  `_gate` (`AttachSession` `:297`, `DequeueForFlush` `:331`). For a lock-free reader they need volatile
  access and an explicit publication order (`Session` before `Ready`); the setup-window path
  (`EnqueueSetupDatagram`, `:171-223`) must keep running under `_gate` and must **re-check `Ready` after
  taking the gate**, otherwise a datagram read as "not ready" can be enqueued after the flush already
  flipped the slot ready and drained — leaving it to sit until the TTL.
- **`UdpProxySession._activityGate` is not needed on the send or touch paths.**
  `QuiescenceScope.TryEnter` (`QuiescenceScope.cs:78-95`) is itself a lock-free CAS and is the real
  admission authority; `DrainAsync` joins outstanding leases, so a sender that entered before the seal
  is waited for. The lock's documented job (`UdpProxySession.cs:98-104`) is ordering the `_expiring` flag
  against the scope facts for `State`; that stays for `TryBeginExpiry`/`CancelExpiry`/`State`, which are
  off the packet path. **Removing it widens one real window**: a sender that reads `_expiring == false`
  between the sweeper's `IsIdle` re-check (`:198`) and its `_expiring = true` store (`:199`) is admitted,
  so the datagram **goes out** where today it becomes a counted `UdpFailClosedDrop`
  (`Send.cs:150-155`, `RuntimeCounters.UdpFailClosedDrop`). Benign direction (a datagram on a session
  that is about to expire is delivered, and `DrainAsync` still joins the lease so the transport cannot be
  freed under it), but it is an accepted delta with a name, and the two spec rows that describe the old
  order must be restated: `udp-relay.md:471` ("lease taken under `_activityGate`") and
  `async-lifetime.md:182-183` ("an owner may hold its own gate across `TryEnter` (e.g. `UdpProxySession`)")
  — see `design.md` §9's spec-row map.
- **The other two bucket legs at this site** must be in the change or the clock is only half removed:
  `UdpProxySession.TryBeginExpiry` (`:194-207`, the `now - LastActivityUtc < idleTimeout` guard under
  `_activityGate`) and `UdpProxyCoordinator.RemoveExpiredAsync`'s candidate scan (`:369`, which compares
  `session.LastActivityUtc`). The scan may stay `DateTimeOffset`-shaped only as a **pre-filter** for
  `TryBeginExpiry`'s bucket-space re-check; move both to the bucket form to avoid two comparison shapes.
- **The transport `_sendGate` is out of scope** (it serialises the shared send buffer and the relay
  rebind; `Socks5UdpTransport.cs:393-399`). Requirement 5 does not name it and the recorded benchmark
  measures the coordinator path with a fake transport by design
  (`benchmarks/results/2026-09-29-benchmark-coverage/README.md`, R1b).

## 7. Which benchmark arm proves which claim, and where the fake arm lies

| Claim | Arm | Recorded baseline | Notes |
|---|---|---|---|
| Req 1/2/3 scaling (series) | `scaling.contention` **warm** arm — **landed in Step 1**; the pre-change series is 0.161 / 0.154 / 0.153 with 3.29 M/s at four threads, and the reverted concurrent-index patch reached **0.628 / 11.06 M/s** (§13.1) | the older `real` arm at 4 threads: 2,699,595 /s, ratio 0.203 (`scaling-contention.jsonl`) | the amended PRD makes the warm arm's self-normalised ratio the acceptance line (≥0.6) with its own 1-thread arm ≥3.00 M/s and the recorded-baseline reading (≥7,996,220 /s at 4 threads) beside it; the arm must also report its cache hit/miss counts (§13.3) |
| Req 2's counter-proof (exact) | a counting guard: 0 exact-tuple probes on N warm hits, exactly M full checks for M claims; plus the wildcard fact | `NeverOwnedGuard` arm vs real arm at 1 thread: 6,766,983 /s vs 3,331,758 /s ≈ +150 ns per lookup | **the fake arm's distortion**: `NeverOwnedGuard` (`BenchmarkShared.cs:196-199`) answers without touching the registry, so every dispatcher row built on it (`DispatcherBenchmarks.cs`, all four warm rows) reports a constant zero delta for the reorder. The fake guard must also gain `IsWildcardOwned` (returning false) or the warm arm cannot be shaped |
| Req 3's clock row | `FlowTableProductionShapeBenchmarks.ReadActivityClock` | 40.25 / 40.74 / 40.56 ns, 0 B | the row is a bare `TimeProvider.System.GetUtcNow()` (`FlowTableBenchmarks.cs:163-169`); after the change it measures code the product no longer runs and must be retired or marked obsolete |
| Req 1/4 exact counts | not yet measured | — | needs the new gate/probe counters of `design.md` §9 |
| Req 5 (series) | `udp-ready-path-contention` | 361.4 / 403.6 / 523.3 ns at 1/2/4 workers, 0 B (`--job short`) | +40–45 % from 1 to 4 workers is the contention claim; the transport gate is excluded by the fake transport **on purpose** and that is stated in the README. The amended PRD's line is the 4-worker mean ≤261.7 ns; the 4/1-worker ratio is a host metric (recorded 1.45) and is not a criterion |
| Memory (floor + full capacity) | `residency-census.jsonl` `flowTable` stage | the stage's delta over its baseline stage is **32,513,544 / 32,519,352 / 32,525,832 B** across 3 runs at capacity 65,536 with 100 live flows (research §A1's ~20 MB estimate is superseded) | the CD floor drops to ~200 KB empty; at full capacity the node sets are ≈23–27 MB (~1.2× the entry arrays replaced, still under the measured 32.5 MB today) |
| Must-not-move series | `tcp-redirect-data-path` (all 16 cases), `flow-table-production-shape` hit rows, `gc-soak` shape anchors | 51–119 ns per rewrite row; 92.7–117.3 ns same-orientation | the redirect rewrite rows do not include the table lookups, so requirement 4 has no series evidence available — its proof must be a count |

**The ratio trap, and the amended PRD's resolution.** `ScalingContentionScenario.BuildVerdict` computes
`scalingRatio[i] = rps(i) / (i × rps(1))` (`ScalingContentionScenario.cs:224`) — the denominator is the
**same run's one-thread arm**, so removing per-lookup work raises the bar the ratio sets: with a
post-change one-thread rate `X`, the four-thread arm must reach `2.4 × X` for 0.6. The amended PRD
resolves the ambiguity by naming exactly one acceptance line — the warm arm's **self-normalised**
4-thread ratio ≥ 0.6 with its own 1-thread arm ≥ 3.00 M/s — and keeping the recorded-baseline number
(3,331,758 /s ⇒ ≥ 7,996,220 /s at four threads) as the comparability reading recorded beside it. Both
must appear in the verdict row; neither may be recomputed by hand from a different arm. This is the same
class of measurement error as F3's D10/D13 (a metric whose denominator moved with the fix), and it is
called out in `design.md` §10.3–10.4.

**The arm-count arithmetic is part of the criterion.** `windowSeconds = Math.Max(1,
options.DurationSeconds / (2 * threads.Length))` (`:41`) hardcodes **two** arms. Adding the warm arm makes
three, so the divisor must become `arms.Length * threads.Length`; the before/after series then run
`--quick --duration 63` to keep each configuration at the recorded 7 s window (3 arms × 3 thread counts ×
7 s = 63 s). Left uncorrected, `--duration 45` silently shortens every window to 5 s and breaks
comparability with the recorded series while still "passing".

## 8. Correctness hazards per candidate mechanism

### 8.1 Lock-free reads over a plain `Dictionary` — **forbidden, with runtime-source evidence**

The documented contract is "not thread-safe"; the concrete failure is worse than a wrong answer.
`Dictionary<TKey,TValue>.Resize` (dotnet/runtime `System.Private.CoreLib`,
`src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs`; line numbers below
verified against the **`release/10.0`** branch, i.e. the installed .NET 10 runtime — the `main` branch has
the identical shape a few lines earlier) publishes the arrays **in this order**:

```csharp
Entry[] entries = new Entry[newSize];          // :1255 new, larger entries array
Array.Copy(_entries, entries, count);          // :1258 copy, then rehash into it
_buckets = new int[newSize];                   // :1275 new buckets published FIRST
_fastModMultiplier = ...;                      // :1277 new size/multiplier
for (int i = 0; i < count; i++) {
    ref int bucket = ref GetBucket(entries[i].hashCode);
    entries[i].next = bucket - 1;              // :1284 chains rewritten in the new array
    bucket = i + 1;
}
_entries = entries;                            // :1290 new entries published LAST
```

A concurrent `FindValue` reads `_buckets`/`_fastModMultiplier` at `:414` and then
`Entry[] entries = _entries` at `:415` — between `:1275` and `:1290` it mixes **new buckets with the old,
shorter entries array**, and the new chain values are indices into the new array. The runtime guards each
step (`if ((uint)i >= (uint)entries.Length) goto ReturnNotFound;`) and then detects the damaged chain
explicitly (`collisionCount <= entries.Length` … `goto ConcurrentOperation` with the comment "The chain
of entries forms a loop; which means that a concurrent update has happened. Break out of the loop and
throw, rather than looping forever") → `ThrowInvalidOperationException_ConcurrentOperationsNotSupported()`
at `:479` → **`InvalidOperationException`**. The same loop can form under a concurrent
`Add`/`TryInsert` (which rewrites `entries[i].next` and the bucket head), i.e. under an ordinary flow
claim, not only a resize.

For contrast, `ConcurrentDictionary.TryGetValue` (`System.Private.CoreLib/.../ConcurrentDictionary.cs:517-548`)
reads `Tables tables = _tables;` **once** (`_tables` is `private volatile Tables _tables;`, `:31` — one
coherent snapshot of buckets + locks + comparer), then
`GetBucket(tables, hashcode)` (`:2224-2235`, a volatile read of `tables._buckets`) and walks the node
chain via `n._next`. No lock is taken anywhere in the method, and no chain is ever rewritten in place:
nodes are published once with their `_next` already set.

On the pump thread that exception is not a dropped packet: `CapturePacketProcessor.ProcessAsync` rethrows
(`CapturePacketProcessor.cs:87-97`), `NdisCapturePump.InvokeHandler` propagates it into `RunIteration`
(`NdisCapture.cs:299-306`), the run loop records it as a failure and exits, and
`MultiAdapterCaptureLoop` cancels every sibling pump and rethrows — capture is torn down process-wide.
So "read the dictionary without the lock and validate afterwards" is out; the *index data structure* has
to be one whose read path is specified to be concurrent.

### 8.2 The ABA hazard on pooled `FlowState` — reachable today, fatal for a naive lock-free read

`FlowState` is a pooled mutable object re-initialized **in place** (`Domain.cs:176-182`), and the pooling
contract explicitly says a reference held past expiry can observe the next flow's fields
(`hot-path.md:485-489`, "callers must read state within the gate-held / `Touch`-refreshed operation").

- **Today, under the lock**, the reader's hold spans `TryResolve` → `state.Touch(...)` → the dispatcher's
  `existing.Decision` / `existing.Key` / `existing.Generation` reads (`FlowDispatcher.cs:165`, `:173`,
  `:174-175`, `:181`) — all after `_gate` has been released. The sweep (`RemoveExpired`) can remove the
  state, return it to `_freeStates`, a concurrent claim can rent it and `Reset` it for another flow, all
  while the dispatcher still holds the reference. The window is a few instructions wide but a thread
  preemption inside it widens it arbitrarily. **This is a pre-existing latent defect** (no test exercises
  it, no measurement would see it); the same shape exists on the claim path (`TryClaimResolved` returns
  the state at `FlowTable.cs:163` and `DispatchSlowAsync` reads `claimed.Decision`/`claimed.Generation`
  at `:233`–`:243` after the gate is released).
- **With a lock-free read** the window becomes the normal path, so the design must make the returned
  value safe by construction (`design.md` §2.3: a validated snapshot, or no pooling). Dropping the pool
  fixes it but breaks two exact gates (`HotPathAllocationGateTests.cs:415-461`,
  `:463-478`) and the documented pooling contract; the validated snapshot keeps them green, *and* the two
  gates keep their exact windows because the claim-side diagnostics accessor of `design.md` §2.2 returns
  the same instance without allocating.
- `Touch` itself is safe either way: it only writes the activity field, and a write into a
  recycled or retired state is harmless (the state is either brand-new or unreachable). The stored value
  can be **one bucket newer** than the one `Reset` wrote if the clock ticked in between — still a
  monotone-forward write of a value any concurrent warm hit would write.

### 8.2b The seqlock must be barrier-correct, not just x86-correct

`Volatile.Write` is **release-only**: it orders the accesses that precede it, never the field stores that
follow. A writer protocol that is just `Volatile.Write(odd); fields…; Volatile.Write(even);` therefore
does not prevent a reader on a weak memory model (ARM64) from observing *new* fields while both version
reads still return the old even value — a mixed triple accepted as valid, which is exactly the false hit
§8.2's remedy exists to forbid. The product's shipped tree is x64, where TSO makes the missing barrier
invisible, which is precisely why the protocol must state the barrier explicitly rather than rely on the
host. Required writer shape (and the mirrored reader shape): publish odd → full fence
(`Interlocked.MemoryBarrier()`, StoreStore on ARM64) → field stores → full fence → publish even; reader:
acquire read → full fence → field reads → full fence → acquire read. The version is monotone `++` (never
reset to 0); wraparound is harmless because the reader compares the two reads for equality and checks
parity. Recorded in `design.md` §2.3, which is the authority for the landed protocol.

### 8.2c The ABA case that is deliberately accepted

If the state is removed and **re-claimed for the same key** inside the reader's window, version bracketing
and key corroboration both pass and the view carries the newer publication. That is correct: the view
means "the decision currently published for this key", policy per key is deterministic, and
`Generation` is trace-correlation only (`FlowDispatcher.cs:175`, `:181`, `:233`) — nothing routes on it.
What corroboration rejects is the *key mismatch* case (another flow's decision), which is the only one
that could misroute a packet.

### 8.3 Claim/remove races against a lock-free reader

- The sweep removes and *then* the mapping disappears; a reader that probed before the removal returns
  the decision the flow had — the same answer the gate would have given a nanosecond earlier. The only
  hard rule is that the removal must keep un-publishing **before** the state can be re-`Reset`
  (`FlowTable.cs:284-289`), which is F3's existing order.
- The `_liveStates` registry is only mutated under `_gate` (`:161-162`, `:287`) and is never read by the
  resolve path, so the F3 registry invariant is untouched by this task. `LiveStateCountForDiagnostics`
  and `AssertRegistryConsistent` stay as they are.
- The sweep's removal re-check (`RemoveCandidateAt`, `:281-284`) must stay; with a lock-free `Touch` a
  candidate can be revived between the scan and the removal hold, and the re-check is what keeps the
  "held/revived entries keep their idle point" behaviour.

### 8.4 `Count` / `Capacity` semantics

`Count` (`FlowTable.cs:60`), `Capacity` (`:55`) and `LiveStateCountForDiagnostics` (`:67`) are
gate-consistent snapshots today. Keeping every *mutation* under `_gate` (claims, removals, registry)
means `Count` can stay `lock (_gate) return _count` and remains exactly gate-consistent — the lock-free
read path does not weaken it. `Capacity` stays the fail-closed bound: `TryClaimResolved`'s
`_states.Count >= Capacity` check (`:150`) must become a maintained counter (a `ConcurrentDictionary.Count`
would acquire every lock), and the counter must move under the same gate as the add. The same argument
applies to the UDP coordinator's session capacity (§6).

### 8.5 Other hazards worth a test

- **`FlowState` field tearing.** `Key` is a 96-byte record struct; a reader can observe a partially
  written key if a `Reset` overlaps its reads. Any scheme that only compares the key (no version) is
  unsound; the version is what makes the read validated.
- **Backwards clock.** `TimeProvider.System.GetUtcNow()` can step backwards, and a bucketed `Touch`
  would then *lower* a stamp, expiring every flow at once. The current code has the same property (it
  overwrites the stamp with an earlier `DateTimeOffset`), so this is a preserved behaviour, not a new
  one — but a monotone-guard (`Touch` keeps the max) is cheap and should be considered in the design
  review rather than assumed.
- **Bucket staleness.** The bucket is refreshed per pump iteration; if the composition fails to wire the
  per-iteration tick the bucket freezes and every flow looks idle at the next sweep. The design must have
  the two further refresh sources (claims, sweep) **and** name the wiring concretely — the only chain
  point is `Program.cs:284` → `DurableCaptureBundle.FlushPendingInjections`
  (`DurableCaptureBundle.cs:362`), because `FlowDispatcher.cs:113` builds its own `FlowTable` with no
  clock and the coordinators are composer-built (`DurableCaptureBundle.cs:188-244`) — plus an exact test
  that drives the *composition* and counts one tick per pump iteration.
- **The clock must be reachable by every consumer.** One `ActivityBucketClock` created in
  `DurableCaptureBundle` and threaded into (a) `FlowDispatcher` → its `FlowTable`, (b) the
  `TcpRedirectTable`/`TcpProxyCoordinator`, (c) `UdpProxyComposer` → `UdpProxyCoordinator` →
  `UdpProxySession`. A per-table clock would let the stamps and the sweep cutoffs drift apart.
- **Sub-bucket and zero idle timeouts.** See §4 items 8–9: `idleTimeout ≤ 0` must keep "retire all", and
  the propagation sentinel must not be `0`.
- **`_sessionCount` vs `_sessions.Count`.** Any `_sessions` mutation that skips the counter produces a
  permanently wrong capacity gate (fail-closed for too few, unbounded for too many). Needs a churn test.
- **`ConcurrentDictionary.Count`** acquires every bucket lock; it must never appear on a packet path or
  under another lock. Three sites need maintained counters: `FlowTable`, `TcpRedirectTable`,
  `UdpProxyCoordinator`.

## 9. Recorded discrepancies (code wins)

| # | Source claim | Code reality | Effect |
|---|---|---|---|
| D1 | PRD req 1 "no process-wide lock on the warm resolve" is presented as reachable by reordering/wrapping the existing lookups | the existing indexes are `Dictionary` (`FlowTable.cs:19-20`, `TcpRedirectTable.cs:157-159`), whose read path is not concurrent-safe (runtime source cited in §8.1) | req 1 is reachable **only** by changing the index type (concurrent map) plus a validated read of the pooled state — **now written into the amended PRD req 1**; `design.md` §2 |
| D2 | Research F2.1/A4 "cache 'proven not self' in the `FlowState`" | the claim-time check at `FlowDispatcher.cs:194` already precedes every claim, and a self-owned **exact** tuple never produces a state | no new field; the amended req 2 keeps the **wildcard** half on the warm path (`IsWildcardOwned`), so the change is one interface member plus one call site, not a deletion |
| D3 | F3 contract item 3 "one bucket per pump iteration carried on `CapturedFlowPacket`/`FlowContext`" | the pump has a per-iteration seam already (`OnBatchCompleted`), reached from `Program.cs:284`; `FlowDispatcher.cs:113` has no clock and the coordinators are composer-built | implemented as one shared `ActivityBucketClock` created in `DurableCaptureBundle` and threaded to all consumers; `design.md` §3.1 |
| D4 | F3 contract item 4 "granularity ≥ 1 s and ≤ shortest idle timeout / 8" | `MinimumUdpSessionIdleSeconds = 5` → /8 = 0.625 s, so the two halves contradict | width fixed at 500 ms and the contract restated (sanctioned by the amended PRD req 3) |
| D5 | F3 contract item 5 "the bucket is for flow/association idle expiry only; other consumers keep their representation" | the per-packet clock reads that must disappear are at `TcpProxyCoordinator.cs:386`, `Injections.cs:305` and `UdpProxySession.cs:412` — all three feed a *stored* `DateTimeOffset`/`long`; the UDP session is now explicitly in scope | stored representations kept (except the session's source), **sources** become the bucket; the amended PRD req 3 names the three consumers |
| D6 | PRD req 4 "TCP pays one gate entry per packet for the reverse-candidate + resolve pair" | the pair is a redundant `ContainsKey`+`TryGetValue` on `_byReverse`, **and** the fold must delete `Injections.cs:305` or the probe count stays 2 | met with **zero** gate entries on the packet path; the exact proof is a count (gates 2→0, probes 2→1) |
| D7 | PRD req 5 "the session lookup + ready check takes at most one `TryEnter`-style attempt" | `Monitor.TryEnter` semantics would drop datagrams whenever the sweeper holds the coordinator gate; the lookup can be a concurrent-map read instead | met with zero gate attempts; `design.md` §6; the two spec rows describing the old order are restated |
| D8 | F3 defect D9 (`Reset` stamps `DateTimeOffset.UtcNow`) | still present at `Domain.cs:181` | fixed by routing every reset/touch through the injected `ActivityBucketClock` |
| D9 | PRD req 7 "`HotPathAllocationGateTests` … must stay green" | two facts in that class *depend* on the **claim** yielding the pooled instance (`:415-461` asserts 0 B for claim+expire, `:463-478` asserts identity recycling) and `hot-path.md`'s Tests Required names them | the view breaks the direct read; `TryClaimResolvedStateForDiagnostics` (allocation-free) keeps both windows exact — no relaxation, and the accessor's own byte cost is measured inside the window |
| D10 | The scaling criterion's ratio "improves from the recorded 0.203 to ≥ 0.6" | the scenario normalises against its own one-thread arm (`ScalingContentionScenario.cs:224`) **and** `:41` hardcodes two arms | resolved by the amended PRD (self-normalised ratio + 1-thread floor as the line; recorded-baseline number beside it); the arm count is corrected and the series runs `--duration 63` |
| D11 | Research F2 table "`SelfTrafficRegistry.IsOwned` … up to 4 dictionary probes" | exactly right, but the load-bearing part is the **gate**: 3.33 M vs 6.77 M resolutions/s at one thread, ≈ +150 ns | the fake-guard rows cannot show the reorder (§7); the fake guard needs `IsWildcardOwned` to shape the warm arm |
| D12 | Not in any source document: the pre-existing post-gate ABA on both `TryResolve` and `TryClaimResolved` return values | see §8.2 | a defect this task must fix while it is in the area, and the reason the return type changes |
| D13 | PRD note "keeps being proxied until that flow expires" (requirement 2's delta) | every warm hit re-touches (`FlowTable.TryResolve` → `Touch`), so the state survives **while traffic flows**; the 5-minute idle timeout (`IdleExpirySweeper.cs:59`) only starts after silence | the bound is restated as "for as long as the flow keeps receiving packets"; both registration sites named (`TcpProxyRelay.cs:36-44` before its SYN, `TcpRedirectSetup.cs:179` after the claim) |
| D14 | "red before the change" for the retained wildcard half | the unmodified tree already passes it (the old warm entry runs the full check); only the naive deletion fails it | the red is the **naive-deletion variant**, run and logged in Step 4; recorded so the PRD's wording is not read as an unmodified-tree red |
| D15 | `idleTimeout == TimeSpan.Zero` means "expire everything on this call" | a strict bucket comparison keeps a current-bucket stamp alive to the next edge (≤500 ms), and three call sites drain with it (`UdpChurnScenario.cs:234`, `UdpSessionBudgetRun.cs:97`, `SessionSetupDecompositionBenchmarks.cs:249`) | explicit `cutoffBucket = long.MaxValue` rule for non-positive timeouts |
| D16 | `Count` can keep reading the dictionary | `ConcurrentDictionary.Count` acquires every bucket lock | maintained counters at all three sites, read under the gate |
| D17 | The propagation sentinel `0` = "never propagated" | bucket 0 is a real bucket and `UdpProxySessionTests` starts at `UnixEpoch` (`:23`, `:38`), so a `0` sentinel suppresses the first propagation | sentinel becomes `long.MinValue` (or a flag) |
| D18 | "the bucket's never-early bound covers the association stamp" | `UdpAssociationPool` retirement is lease-based (`UdpAssociationPool.cs:75`, `UdpControlAssociation.cs:175-181`) and the only `LastActivityUtc` reader is test-only (`UdpAssociations.cs:138`) | the claim is scoped to the legs that consume activity in production; the observer's stamp may be up to 500 ms older than true activity |
| D19 | Spec rows touched by this design | `hot-path.md:38/500`, `tcp-local-redirect.md:24/166`, `traffic-policy-lifecycle.md:48`, `udp-relay.md:471`, `async-lifetime.md:182-183`, `quality-guidelines.md:17/21/22/44` all state the pre-change behaviour | each is restated in Step 7; `design.md` §9 carries the spec-row → proof map so none can be missed silently |

## 10. Dead ends (recorded against re-exploration)

1. **Lock-free reads over the existing `Dictionary`** — §8.1; the runtime throws
   `InvalidOperationException` on the torn chain and that tears down capture.
1b. **A concurrent index (`ConcurrentDictionary` for `_states`/`_transportIndex`) — measured and
   rejected (§13).** It delivered the acceptance ratio (0.628; 11.06 M/s at four threads) and cost
   **488 B per claim+expire cycle** (3 nodes × ~160 B), failing the exact 0 B claim/expire gate, which
   protects the in-place `FlowState` pool. The same argument excludes it at the redirect table (three
   index writes per claim) and at the UDP coordinator (the admission insert sits inside
   `UdpSetupEnqueuePathAllocatesNoManagedBytes`). Its numbers are kept as the **ceiling comparator** for
   the replacement, never as a fallback.
2. **Copy-on-write index** (publish a new dictionary per claim) — `O(N)` copy per claim with a
   65,536-entry table, and ~20 MB of churn; also allocates on the claim path.
3. **A direct-mapped read cache in front of the locked dictionary** — a fixed `FlowState?[]` probe by
    hash, validated by the state's key, misses fall back to the locked path. Cheap, but a collision
    makes the warm resolve take the gate again, so it cannot carry the *structural* proof requirement 1
    asks for, and two structures have to be kept coherent (claim insert, removal clear, sweep clear).
4. **Dropping the `FlowState` pool** (allocate per claim, blessed by research §A4 item 7) — would make
   the lock-free read trivially safe but turns `FlowTableClaimAndExpireCycleAllocatesNoManagedBytes`
   (`HotPathAllocationGateTests.cs:415-461`) and `FlowTableRecyclesExpiredStatesThroughItsPool`
   (`:463-478`) red, breaks the documented pooling contract (`hot-path.md:485-489`), and adds gen0
   garbage on the claim path in a process configured with a 128 MiB `HeapHardLimit`.
5. **A "dirty latch" instead of a timestamp** (`Touch` sets a flag; the sweep clears it on the first
   candidate tick and removes on the next) — zero clock, zero lock, but the retention envelope becomes
   `(idle + interval, idle + 2 × interval]` and the F3 contract item 1 (monotonic integer stamp) is not
   satisfied.
6. **`Environment.TickCount64` (or `Stopwatch.GetTimestamp`) inside `Touch`** — cheaper than
   `TimeProvider.GetUtcNow()` but still a clock read; it would pass the injected-clock proof while
   violating the requirement's meaning. Rejected as proof-gaming.
7. **A dedicated sub-second refresher thread/timer for the bucket** — a new lifetime, a new disposal
   path and a new failure mode for a value the pump iteration already produces for free.
8. **Per-stripe locks with a direction-normalised hash** (research F2.3) — unnecessary once reads are
   lock-free, and it is the sharded rebuild the PRD puts out of scope.
9. **Bucketing tombstones, the setup cooldown, pending-SYN TTL or the UDP setup queue stamps** — F3
   contract item 5 keeps them, and none of them is on the warm path once §6's reorder lands.
10. **`Monitor.TryEnter` on the UDP coordinator gate** — see D7; it converts sweeper contention into
    datagram loss.
11. **Moving the self-traffic check into `IProcessAttributor` or the reverse hook** — both run later on
    the slow path than `TryHandleSelfTrafficAsync` (`FlowDispatcher.cs:194`), and the reverse hook must
    not see self traffic first (a self tuple that looks like a listener port would be reverse-handled).

## 11. Reproduce-before commands and their recorded numbers

All numbers quoted from `benchmarks/results/2026-09-29-benchmark-coverage/` (host: NixOS 26.11, .NET
10.0.12, Ryzen 9 9955HX, 32 logical/16 physical; the README's rule applies — allocation and counts are
gates, timing/throughput are series and <2× is noise).

```bash
# R1 scaling (the criterion's series). RECORDED baseline: 2 arms ⇒ 45 s total, 7 s window per
# configuration. After the warm arm lands: 3 arms ⇒ --duration 63 keeps the same 7 s window (the
# scenario's windowSeconds divisor must be corrected to arms.Length * threads.Length, see §7).
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario scaling --quick --flows 4096 --duration 63 \
  --output benchmarks/results/<task>/scaling-contention-<series>.jsonl     # truncates per process

# R1b UDP ready path (BDN short job; the artifact pair is overwritten per run).
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*UdpReadyPathContention*' --job short

# R2 flow-table production shape (the ReadActivityClock row that must be retired).
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*FlowTableProductionShape*' --job short

# R5 composed redirect rewrite (must not move; no table lookups in the number).
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*TcpRedirectDataPath*' '*Parser*' --job short

# One pinned thread count for a single-point check.
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario scaling --quick --flows 4096 --duration 20 --threads 4 --output /tmp/scaling-4t.jsonl
```

| Series | Recorded |
|---|---|
| `scaling.contention` real 1/2/4 threads | 3,331,758.3 / 3,398,317.4 / 2,699,595.2 res/s → ratios 1 / 0.510 / 0.203 |
| `scaling.contention` fake 1/2/4 threads | 6,766,982.5 / 4,641,783.9 / 4,273,209.5 res/s → ratios 1 / 0.343 / 0.158 |
| `udp-ready-path-contention` 1/2/4 workers | 350.2–361.4 / 403.6–405.8 / 498.8–523.3 ns, 0 B every row |
| `FlowTableProductionShape` | same-orientation 92.65–117.29 ns; reverse alias 134.80–138.42 ns; `ReadActivityClock` 40.25–40.74 ns; 0 B |
| `tcp-redirect-data-path` | IPv4 51–129 ns, IPv6 87–122 ns across 16 rows, 0 B |
| sweep/gate totals at this tree | per-gate proof totals string `HotPathAllocationGateTests:11 CapturePumpReadCallTests:3 SweepAllocationGateTests:12 NdisCapturePumpTests:14`; suite `995 + 18` (F3's check session) |
| scaling parameters | `flows=4096`, `sharedKeyCount=409`, `sharedKeyPercent=10`, `windowSeconds=7`, `selfTrafficTuples=32`, `BatchSize=64` |

## 12. What F3 changed that this task must not break

- `_liveStates[0.._liveCount)` is a hole-free mirror of `_states`, appended under `_gate`
  (`FlowTable.cs:161-162`) and swap-removed at the cursor (`:287`), removal strictly before the state
  could be recycled (`:288`). `LiveStateCountForDiagnostics` takes the gate so a churn test can assert
  `== Count`.
- `RemoveExpired` = one round per call at minimal hold granularity: scan hold ≤ `SweepChunkEntries` = 256
  examinations stopping **on** the first idle-elapsed candidate (`ScanChunk`, `:241-268`), predicate with
  no lock held (`:216`), one removal per removal hold with the identity/idleness/key re-check
  (`RemoveCandidateAt`, `:277-295`), `_sweepGate` outer to `_gate` (`:190`), `HoldProbe` counts
  (`:94-121`).
- The accepted evidence is the countable probe (`SweepHoldProbe`), not any wall-clock reading; every
  timing field in the F3 artifact is `gated: false` with the calibration control quoted beside it.
- Do not add a per-entry caller callback under `_gate`, do not allocate in a hold, do not let a hold
  re-arm activity, and keep `Count`/`Capacity`/the fail-closed capacity behaviour as they are.

## 13. Step 3's measured reversal: the concurrent index is out, the direct-mapped cache is in

Written after Step 3 was implemented, measured and reverted on this tree (Steps 1 and 2 landed; the
mechanism is the operator's replacement, `design.md` §2).

### 13.1 What the concurrent index achieved, and what it cost

| measurement | value |
|---|---|
| warm arm, self-normalised 4-thread ratio, **before** (3 runs) | 0.161 / 0.154 / 0.153 |
| warm arm, 4-thread rate, **before** | 3.29 M/s |
| warm arm, self-normalised 4-thread ratio, **with the concurrent index** | **0.628** |
| warm arm, 4-thread rate, **with the concurrent index** | **11.06 M/s** |
| `WarmResolveTakesNoFlowTableGateEntries` | red (`Expected: 0, Actual: 256`) → **green** |
| the parked-gate fact (`WarmResolveCompletesWhileFlowTableGateIsHeld`) | queued behind the gate → **green** |
| `HotPathAllocationGateTests.FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` | `Expected: 0, Actual: 124,928` over 256 cycles = **488 B per claim+expire cycle** |

**The 488 B factorization** (why it can never be fixed by tuning): a bare
`ConcurrentDictionary<FlowKey,int>` add+remove measures **160 B/cycle** for its `Node`; the flow table
inserts **three** nodes per claim (`_states`, `_transportIndex[tuple]`,
`_transportIndex[tuple.Reverse()]`) → 3 × 160 = 480 B, plus ~8 B of incidental overhead = 488 B. Node
allocation happens on *insert* and a removed node is never reused, so the exact 0 B claim/expire gate can
never hold while the table is backed by a `ConcurrentDictionary`. The gate protects the deliberate
in-place `FlowState` pool (`hot-path.md` "FlowTable pooling"); the parent programme's PRD forbids
regressing an allocation gate, so the 488 B is **not** sanctioned and the gate is **not** re-baselined.
The variant is recorded as a measured dead end (§10.1b) and its numbers are kept as the ceiling the
replacement has to approach.

The same argument rules the concurrent index out at the other two sites: `TcpRedirectTable` would
allocate a node per index write (three of its four indexes are written per claim), and
`UdpProxyCoordinator` would allocate a node in the admission window that
`UdpSetupEnqueuePathAllocatesNoManagedBytes` measures.

### 13.2 The replacement: a pre-allocated direct-mapped warm cache over the gated authority

- **Slot array** `FlowState?[] _warm`, allocated once in the ctor with
  `slots = RoundUpToPowerOf2(clamp(capacity * 64, 4_096, 262_144))` (32 KB … 2 MB; 2 MB at the shipped
  default capacity). Never grown, never resized, no per-claim allocation anywhere.
- **Slot index** is an *order-independent* mix of the endpoint pair (`FlowHash.CombineCanonical`, i.e.
  `FlowHash.Combine` over the two endpoints ordered by `(Address.Bits, Port)`), so a packet and its
  reverse select the same slot — the F2.3 "direction-normalised hash" property, in cache form, and the
  reason the `_transportIndex` alias needs no second probe.
- **Hit validation is exact and uses Step 2's landed machinery**: the cached state carries its own full
  `FlowKey`, `FlowState.TrySnapshot` (`Domain.cs:208-230`) returns the barrier-bracketed snapshot, and the
  probe accepts it only when the snapshot's transport tuple matches the queried tuple in either
  orientation. The equivalence to the two dictionary probes rests on one invariant worth its own test:
  **the table holds at most one state per transport tuple** (`AddToTransportIndex` uses `Dictionary.Add`,
  which throws on a duplicate, and `TryResolveLocked` resolves a same-tuple claim to the existing state).
- **Population**: `TryClaimResolved` (after the index writes), and write-through in `TryResolveLocked` on
  a gated hit. **Invalidation**: `RemoveCandidateAt` — the table's only removal path — clears the slot it
  owns (`ReferenceEquals`) before `ReturnState`. All three writes are `Volatile.Write` under `_gate`.
- **Failure mode**: a colliding flow's slot is overwritten, so that flow falls back to exactly today's
  locked path (a **false miss**, never a false hit); a stale entry after a removal is rejected by the
  same validation; a same-key re-claim serves that key's current decision with `Generation` log-only.
  Research §A5's ban on approximate structures applies to *flow decisions*; this cache is exact — its
  only lossy dimension is which flow is resident.

### 13.3 The arithmetic the acceptance now depends on

The miss rate under a round-robin walk is the fraction of flows that share a slot with another flow,
`m ≈ 1 − e^(−λ)` with `λ = live flows / slots` (with write-through, a colliding pair thrashes: each
lookup re-writes the slot the other just wrote). Two models bracket the ratio between the three measured
points (m = 1 → 0.155; m = 0 → 0.628; the 1-thread/4-thread per-lookup costs 227/362 ns lock-free and
300/1,481 ns locked):

- *linear-cost blend* — `ratio(m) = (227 + 73m) / (362 + 1119m)`, crossing 0.6 at **m ≈ 1.6 %**;
- *gate-saturation* — the gate serves 3.33 M ops/s, so a ~2 % miss rate is only 0.22 M ops/s of demand
  and the locked lookups stay uncontended: `ratio(m) ≈ (227 + 73m) / (362 − 62m)` → 0.63 at m = 2 %.

Both agree that **m ≲ 2 % satisfies the criterion under either reading**, which is why the cache is sized
for λ ≈ 0.016 (2 MB) rather than the operator's illustrative `2 × capacity` — that example gives
λ = 0.4 and m ≈ 33 % at the `scaling` scenario's capacity (5,120), which the models put at 0.39–0.5, i.e.
**below the line**. The honest flip side: the measured ceiling is 0.628, only 4.7 % above 0.6, so the
margin is thin by construction and the run must report both the miss rate and the ratio; if the ratio
lands below 0.6 at a measured m ≤ 2 %, the levers are the slot cap (524,288), K-way probing (a shape
change needing the operator's nod) or an explicit restatement of requirement 1 with the numbers.

### 13.4 The claim/slow-path ABA, deliberately not fixed here

`TryResolve`/`TryClaimResolved` keep `out FlowState?` (so the 0 B gates, the tests and both diagnostics
accessors are untouched), which means the pre-existing latent post-gate ABA on the **slow/claim** path
(`FlowDispatcher.cs:203-243` reads the pooled instance after the gate is released) remains. The warm path
is fixed because it reads the validated view. Fixing the claim half needs the view API on
`TryClaimResolved` (≈15 test call sites) and is out of this step's scope; it is recorded in `design.md`
§2.7/§12 D12 and in the plan's deferred list — not silently dropped.
