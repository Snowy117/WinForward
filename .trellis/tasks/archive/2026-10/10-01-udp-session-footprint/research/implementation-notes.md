# Implementation notes — F6 UDP per-session footprint: relay receive buffer, pool sizing, adaptive idle TTL

Code-grounded survey taken at planning time on the working tree (`master`, after F2/F3/F4/F5/F8
landed). Every anchor below was read from the file it names; nothing is quoted from the research
document or the PRD without checking the code. Where the PRD, the research, or a spec row disagrees
with the code, the code wins and the disagreement is recorded in §9.

Method: `rg` + read over `src/`, `tests/`, `benchmarks/`, the archived research
(`.trellis/tasks/archive/2026-09/09-29-tcp-udp-path-structural-perf/research.md` §F6, §A1 item 4,
§A5, §A6 item 12), the archived measurement task
(`.trellis/tasks/archive/2026-09/09-29-benchmark-coverage-remaining-findings/{prd,design,implement}.md`),
the recorded baselines under `benchmarks/results/` and the specs (`hot-path.md`, `udp-relay.md`,
`traffic-policy-lifecycle.md`, `quality-guidelines.md`, `async-lifetime.md`,
`directory-structure.md`). No product build, suite, format, inspector or benchmark was executed for
this note; every measurement quoted is a recorded artifact with its path.

---

## 1. The three resources, and the one number that makes them matter

| # | Resource | Where it lives | Per session | At the default capacity (16,384) |
|---|---|---|---|---|
| 1 | Relay socket **kernel** receive buffer | `Socks5UdpTransport._socket.ReceiveBufferSize` | 128 KiB (configured) | 2 GiB of kernel memory |
| 2 | Native **receive-window** lease | `NativeBufferPool` (1,537 B payload + 4 B state word = 1,541 B) | one lease, held for the session's whole life | 24.1 MiB native high-water (25.6 MiB with the retire allowance, 1,024 leases) |
| 3 | Parked receive loop + its buffers | `UdpProxySession.ReceiveLoopAsync` | one pending IOCP read + one lease | retention-driven |

Resource 1 is **kernel** memory: it never appears in `workingSetDeltaBytes`, in
`GC.GetTotalAllocatedBytes`, or in any managed-heap artifact. Resource 2 is **native** memory, but a
rent that misses the free list also allocates one managed `NativeMemoryManager`
(`NativeBufferPool.cs:93`) — the only managed trace it leaves, and only on that path. Resource 3 is
the only one the existing footprint instrument describes in the steady state. This asymmetry drives
the whole evidence plan (§8, `design.md` §7).

---

## 2. Site 1 — the relay receive buffer default and every consumer

**The default is two constants that must agree, and nothing asserts that they do.**

- `Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize = 128 * 1024`
  (`src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:174`), documented at `:163-173`.
- `ConfigurationLoader.DefaultUdpRelayReceiveBufferKb = 128`
  (`src/WinForward.Configuration/ConfigurationModels.Udp.cs:66`) and
  `DefaultUdpRelayReceiveBufferBytes = DefaultUdpRelayReceiveBufferKb * 1_024` (`:69`).

The XML doc on the first says "matches `udpRelayReceiveBufferKb`"; the second says "matches
`Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize`". No test asserts the equality — `rg` finds
no comparison of the two. The consumer chain never mixes them (composition always feeds the
*configuration* value), so today they only have to agree for a caller that takes the transport
default directly:

- `Socks5UdpTransportFactory`'s optional parameter default (`Socks5UdpTransport.cs:129`), and
  `Socks5UdpTransport.Create`'s (`:257`) — both `DefaultRelaySocketReceiveBufferSize`.
- `UdpProxyOptions.RelayReceiveBufferBytes` default (`UdpProxyOptions.cs:29`) — same constant.
- `FrameworkSetupBenchmarks.cs:29` — the benchmark's own local copy of the same constant.
- `tests/WinForward.Core.Tests/TestHelpers/UdpTransportTestFactory.cs:24` — the test factory default.

**Where the configured value flows (the real consumer chain):**

1. `ConfigurationLimits.Parse` → `ValidatedConfiguration.UdpRelayReceiveBufferBytes`
   (`ConfigurationLimits.cs:94`, `ConfigurationModels.cs:165`).
2. `DurableCaptureBundle.BuildWithUdpAsync` → `UdpProxyComposition.RelayReceiveBufferBytes`
   (`DurableCaptureBundle.cs:247`; the record member is `UdpProxyComposer.cs:28`).
3. `UdpProxyComposer.Create` → **two** consumers (`UdpProxyComposer.cs:81`, `:91`):
   - `Socks5UdpTransportFactory(..., composition.RelayReceiveBufferBytes)` → applied to each relay
     socket as `socket.ReceiveBufferSize = relayReceiveBufferBytes` **before bind**
     (`Socks5UdpTransport.cs:268`), on a socket constructed per flow (`:267`) and bound at `:272`.
   - `UdpProxyOptions.RelayReceiveBufferBytes` → `UdpProxyCoordinator.RelayReceiveBufferBytes`
     (`UdpProxyCoordinator.cs:75`, `:89`, property `:182`), read exactly once in production by the
     heartbeat's kernel-memory estimate: `(long)bundle.Udp.SessionCount * bundle.Udp.RelayReceiveBufferBytes`
     (`src/WinForward.Cli/Program.cs:316`).

Nothing else reads it. The `udp.sessionBudget` soak's `relayReceiveBufferBytes` row metric is the same
product of `live sessions × the configured buffer` (`UdpSessionBudgetInstrumentation.cs:253`, `:272`,
surfaced by `UdpSessionBudgetRun.cs:309-310`), i.e. **also an arithmetic statement about the
configured value, not a measurement of kernel memory.**

**Validation today** (`ConfigurationLimits.cs`): accepted range 16..1024 KiB (`:49-51`), the DTO is
normalized by `ParseUdpRelayReceiveBufferKb` (`:168-180`), and `WarnOnAggregateRelayReceiveBuffer`
(`:187-192`) warns when `bufferKb > 256` **and** `capacity > 2_048` — both thresholds unchanged by this
task. The separate capacity warning sentence names the aggregate the *default* buffer multiplies into
(`:156`), which is why changing the default moves that sentence's arithmetic (§9/D5).

**The one measurement seam that exists and has never been used:** `internal int
AppliedRelayReceiveBufferSize => _socket.ReceiveBufferSize;` (`Socks5UdpTransport.cs:238-243`), whose
doc says it exists to prove the configured budget "reaches a real socket through the production
`Socks5UdpTransportFactory`". `rg` over `src/ tests/ benchmarks/` finds **no reader**. The task that
wants an assertion that the new default reaches a socket must add that test.

**Test rows that pin the default today** (`tests/WinForward.Core.Tests/ConfigurationLimitsTests.cs`):
`ConfigurationDefaultsUdpBudgetWhenOmitted` (`:123`, compares against the two constants),
`ConfigurationTreatsExplicitNullUdpBudgetAsDefault` (`:134`), `ConfigurationParsesUdpBudgetValues`
(`:156`, asserts the explicit override `udpRelayReceiveBufferKb: 512` → `512 * 1024`),
`ConfigurationWarnsAboveDefaultUdpSessionCapacityWithoutBlocking` (`:171`, `[InlineData(4097, 512)]`
and `[InlineData(16384, 2048)]` — **the aggregate MiB are computed from the default KiB, so these two
numbers move when the default changes**), `ConfigurationAcceptsUdpSessionCapacityAtTheWarningBoundary`
(`:194`), `ConfigurationWarnsOnALargeRelayBufferOnlyAboveBothBoundaries` (`:203`, explicit 256/257 KiB,
unaffected). `UdpProxyCompositionTests.CreateCarriesSessionCapacityAndRelayReceiveBufferAsDistinctValues`
(`:22`) pins the composition seam's transposition guard at `:48`.

---

## 3. Site 2 — the receive-window pool: construction, capacity, overflow accounting, rent/return

**Construction.** `DurableCaptureBundle.BuildWithUdpAsync`:
`var udpWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize));`
(`src/WinForward.Cli/DurableCaptureBundle.cs:233`). `ReceiveWindowSize(cap) = cap + 22 + 1` = **1,537 B**
at the pinned 1,514-byte ABI (`UdpProxyCoordinator.cs:215-216`, constants `:10-11`). **No capacity
argument is passed**, so `NativeBufferPool.DefaultCapacity = 256` applies
(`src/WinForward.Core/NativeBufferPool.cs:22`, used at `:34`).

The pool is bundle-owned and registered for diagnostics (`DurableCaptureBundle.cs:234`, name
`udp.receiveWindow` at `:41`), handed to the coordinator (`:247`) and then to `UdpSessionSetup`
(`UdpProxyCoordinator.cs:100-109`), which puts it in every `UdpProxySessionContext`
(`UdpSessionSetup.cs:118`; context member `UdpProxySession.cs:30`, guarded in the session ctor at
`:83-86` against `ReceiveBufferSize`).

**Rent/return sites — exactly one each.**

- Rent: `UdpProxySession.ReceiveLoopAsync`, `var lease = _receiveWindowPool.Rent();`
  (`UdpProxySession.cs:294`), **once per session**, before the loop; the loop then reuses
  `lease.Memory[.._receiveBufferSize]` for every `ReceiveAsync` (`:302`).
- Return: the same method's `finally { lease.Dispose(); }` (`:332-335`), which covers normal
  cancellation, socket disposal, fault and loop exit. `NativeLease.Dispose` is idempotent across
  copies (`NativeBufferPool.cs:187`, `:106-109`).
- Therefore **one lease per live session, held for the session's whole life** — there is no second
  rent site, no nested rent, and no rent on the send path (`rg 'ReceiveWindowPool'` matches only
  composition, the coordinator's ctor plumbing, and this loop).

**Capacity semantics (the correction that matters for the acceptance gate).**
`NativeBufferPool` is a bounded free list, not a preallocation:

- `Rent` (`NativeBufferPool.cs:72-94`): dequeue a free lease and mark it rented; **only if the queue
  is empty** does it `NativeMemory.AllocZeroed` and `Interlocked.Increment(ref _overflowAllocations)`
  (`:90`). So `OverflowAllocations` counts *rents that missed the free list*, not "allocations beyond
  the capacity". A first fill of N live sessions increments it N times at **any** capacity.
- `Release` (`:106-121`): enqueue when `_inPool < Capacity`, otherwise `NativeMemory.Free`
  (`:112-117`). With `Capacity = 256` and a live population above 256, the free list can never fill,
  so **every session beyond the pooled 256 allocates and frees one native lease per session
  lifetime** — that is the churn the PRD's "every session beyond the 256th runs on tracked overflow
  allocations" describes, and the counter grows without bound across cycles.
- The observable exact discriminator is therefore **overflow growth across a second population
  cycle**, not the absolute counter (see `design.md` §3, `§9/D1`). `Stats` is `internal`
  (`:58-63`), the record is `internal` (`:227-236`), and the tests are friend assemblies, so a gate
  can read `OverflowAllocations`, `InPool`, `Rented`, `Returned`, `Outstanding`.
- `Capacity` itself is a **private** property (`:42`): a test cannot read the pool's capacity
  directly. That is fine — the behavioral rent-cycle gate proves it without an accessor, and adding
  one would widen the surface for nothing.

**Why the capacity must exceed the session capacity: the retire/admit overlap (verified 2026-10-01).**
`ReceiveWindowPoolCapacity(cap) => cap` would be exact only if a retiring session's lease were released
before its slot left `_sessions`. It is not:

- admission is gated on `_sessions.Count >= Capacity` (`UdpProxyCoordinator.Send.cs:72`), so a new
  session is admitted as soon as the slot is removed — not when the lease returns;
- `RemoveSlotAsync` removes the slot under `_gate` (`UdpProxyCoordinator.cs:512-518`) and only then
  awaits `session.DisposeAsync()` **outside** the gate (`:536`);
- the lease returns in `UdpProxySession.ReceiveLoopAsync`'s `finally` (`:334`), i.e. after the loop
  observes cancellation and the socket disposal completes;
- the expiry retire is serial (one `await RemoveSlotAsync` per candidate, `:474-485`), but the
  receive-failure teardown is a concurrent `_scope.Run` child per faulting session (`:546-547`,
  `RemoveReceiveFailedSessionCoreAsync` `:564-575`) and never takes `_sweepGate`; the send-path
  `AssociationLost`/transport-fault removals (`UdpProxyCoordinator.Send.cs:205-210` names the
  classification) and the setup failures each await their own `RemoveSlotAsync` from their own callers.

So concurrent in-flight retirements are **not bounded by 1** and have no in-code bound at all — a
simultaneous multi-session fault is the worst case. A capacity of exactly `cap` therefore re-enters the
tracked-overflow path under exactly the event (a fault storm) the pool exists to smooth. The landed rule
carries a documented allowance (design §3); the in-tree precedent for "size the pool so the steady state
cannot overflow" is the attribution pool, `(int)(FlowAttributionPendingIndex.DefaultGlobalByteBudget /
maximumFrameSize)` with the comment "so the pool can hold the budget's worth of frames without an
overflow allocation" (`DurableCaptureBundle.cs:236-238`).

**Balance contract the new test must keep** (`quality-guidelines.md:47`): rent N → return N → dispose
→ `InPool == N`, `Rented == Returned`, `Outstanding == 0`; plus the L1 "an overflow or exception path
that rents still returns the buffer" case. `NativeBufferPoolTests.PoolDisposalDrainsIdleBuffersAndRentStillWorks`
already covers the type; the new sizing test adds the population-cycle shape.

**What the sizing change does and does not buy.**

- It buys: no per-session native alloc/free churn beyond the pooled population, and a truthful
  overflow counter (today it grows forever, so it cannot be used as a sizing signal).
- It does **not** buy a startup or steady-state memory reduction: capacity is a bound, allocations
  happen on demand, and the pool *retains* its high-water mark (returned buffers are freed only when
  the pool is full or disposed). Worst case at the shipped capacity plus allowance:
  `(16,384 + 1,024) × 1,541 B = 26,825,728 B ≈ **25.6 MiB**` native (16,384 × 1,541 B = 24.1 MiB
  without it) held after the peak, against the 2 GiB → 1 GiB *kernel* change from site 1. At the soak's
  retention-bounded population it is ≈1.5 MiB.
- It does **not** show up in `udp.sessionFootprint`'s first-populate numbers: a first fill overflows
  N times whether the capacity is 256 or N (the second-cycle delta is the signal, `design.md` §3) —
  and the instrument itself must construct its pool through the composition rule to mirror the shipped
  shape (`research/implementation-notes.md` §9/D16).

---

## 4. Site 3 — how the idle TTL is configured, derived and applied today

**Configuration.** `udpSessionIdleSeconds` (`ConfigurationModels.Udp.cs:13`) → validated 5..600 s
(`ConfigurationLimits.cs:59-62`, `ParseUdpSessionIdleTimeout` `:217-225`) →
`ValidatedConfiguration.UdpSessionIdleTimeout` (`ConfigurationModels.Udp.cs:32`, default 30 s at
`:72`).

**Derivation (one instance, one tick loop).** `DurableCaptureBundle.BuildWithUdpAsync` passes
`relayIdleTimeout: configuration.UdpSessionIdleTimeout` to the sweeper
(`DurableCaptureBundle.cs:300`). `IdleExpirySweeper` (`src/WinForward.Runtime/IdleExpirySweeper.cs`):

- `_relayIdleTimeout` (`:64`, default 2 min when not supplied), `_interval` = 1 min (`:61`),
  `s_minimumUdpSweepInterval = 5 s` (`:22`).
- `_udpSweepInterval = DeriveUdpSweepInterval(_interval, _relayIdleTimeout, udpSweepInterval)` (`:65`);
  the pure function is `min(mainInterval, max(5 s, relayIdleTimeout / 2))` (`:78-84`) — the 5 s floor
  applies to the *request*, then the main-interval cap. At the defaults that is **15 s**.
- `RunAsync` ticks on `_udpSweepInterval` (`:97-108`); the main-leg group is gated inside the tick on
  the injected clock (`:121-136`), the UDP leg rides every tick (`:186-188`), and the **attribution
  TTL leg also rides every tick** with no cadence of its own (`:156-168`).

**Application.** `SweepUdpLegAsync` → `_udp.RemoveExpiredAsync(now, _relayIdleTimeout)`
(`IdleExpirySweeper.cs:188`). `UdpProxyCoordinator.RemoveExpiredAsync(now, idleTimeout)`
(`UdpProxyCoordinator.cs:445-493`):

- single-flight through `_sweepGate` (`:448`), reused `_idleScratch` (`:42`, cleared `:454`);
- one `cutoffBucket = ActivityBucket.Cutoff(now, idleTimeout)` (`:455`);
- under `_gate`: `_cooldowns.PruneExpired(now)` (`:459`) then a **pre-filter** collecting sessions with
  `ActivityBucketForDiagnostics < cutoffBucket` (`:460-468`);
- outside the gate: `session.TryBeginExpiry(now, idleTimeout)` (`:477`) is the authoritative re-check;
  on success `RemoveSlotAsync(..., UdpTeardownReason.Expiry)` (`:478`), else `CancelExpiry()` (`:480`).

`UdpProxySession.TryBeginExpiry` (`UdpProxySession.cs:232-247`) recomputes the same cutoff
(`:234`), and under `_activityGate` requires `!_scope.IsSealed && !_expiring && _scope.IsIdle &&
_lastActivityBucket >= cutoffBucket` (`:238`) before setting `_expiring` and cancelling the scope
outside the gate (`:245`). Activity itself is a bucket stamp written by `TouchActivity`
(`:454-467`) from the shared `ActivityBucketClock`, with the association-table observer throttled to
one propagation per 500 ms bucket (`:46`, `:463-466`). Two properties matter for the classification and
both were verified in the review:

- `TouchActivity` is called on **every received datagram** as well as every send (`:311` in the receive
  loop, `:201`/`:213` on the send tails), so an idle clock starts at the *reply*, not at the query —
  the short TTL is measured from the last datagram in either direction, exactly like today's.
- `TouchActivity` itself is lock-free (one `Volatile.Write` plus a CAS-throttled observer), so nothing
  this task adds can put `_activityGate` on the datagram path.

**Two-argument `TryBeginExpiry` has a test caller.** `UdpProxySessionTests.cs:97`
(`IdleExpiryEndsTheReceiveLoopWithoutRecordingAFailure`) calls
`session.TryBeginExpiry(time.GetUtcNow(), TimeSpan.Zero)`. The landed shape therefore keeps a
**delegating two-argument overload** (`oneShotIdleTimeout: idleTimeout`) rather than removing the
signature — which also keeps every F3 caller uniform and needs no test edit.

**The retention contract, verbatim from the code**
(`src/WinForward.Core/ActivityBucket.cs:13-19`): "a state stamped at `t0` retires at age
`idleTimeout + w − (t0 mod w)`, i.e. in `(idleTimeout, idleTimeout + w]`… A non-positive idle timeout
keeps its 'retire everything on this call' meaning". `Cutoff` returns `long.MaxValue` for
`idleTimeout <= 0` (`:48-49`) — the soak's warm-up drain relies on it
(`UdpSessionBudgetRun.cs:97`).

**The F3 sweep work is the regression surface here.** The UDP no-op tick is an exact 0-byte gate over
a populated world (16 fake-transport sessions) —
`SweepAllocationGateTests.UdpProxyCoordinatorSweepAllocatesNoManagedBytes`
(`tests/WinForward.Core.Tests/SweepAllocationGateTests.cs:483`), calling
`coordinator.RemoveExpiredAsync(now, TimeSpan.FromMinutes(5))` (the **two-argument** overload). The
sibling gates are the pool (`:531`) and association table (`:587`). The cadence derivation is pinned
by `IdleExpirySweeperCadenceTests.DerivedUdpSweepIntervalIsHalfTheIdleTimeoutFlooredAtFiveSeconds`
(`:104-110`) and `DerivedUdpSweepIntervalNeverExceedsTheMainInterval` (`:113`). The end-to-end UDP leg
(real sweeper, fake transports, `BeforeExpiryRecheck` counting runs) is
`UdpLegSweepsOnTheFastCadenceWhileTheExpensiveLegsKeepTheMainInterval` (`:26-102`). A new TTL class
must keep all of these green **without changing the meaning of the two-argument overload**.

**The F8 attribution TTL shares the tick.** `FlowAttributionPendingIndex.RemoveExpired` walks a
dictionary against `s_retentionTtl = 5 s` (`src/WinForward.Runtime/FlowAttributionPendingIndex.cs:176`,
`:482-505`). It is wired as the sweeper's `attributionSweep` (`DurableCaptureBundle.cs:300`,
`IdleExpirySweeper.cs:27`/`:156-168`). Any change to the UDP tick period therefore changes the
attribution TTL leg's frequency too — and, unlike the session retention, its **bound tightens**:
`:489` is a raw wall-clock compare (`now - pair.Value.LastWriteUtc > s_retentionTtl`) with no bucket
term, so the actual pending-entry lifetime is `(5 s, 5 s + tick]` — `(5, 20] s` today, `(5, 10] s` after
the tick moves to 5 s. That is the safe direction (an entry that was going to be reclaimed is reclaimed
sooner) and must be recorded rather than left implicit (design §6, and the `error-handling.md:16` row).

**The TCP pending-SYN TTL is *not* affected.** `TcpPendingSynSetupIndex`'s 5 s retention also rides the
idle sweep, but on the **main** leg (`IdleExpirySweeper.SweepMainLegsAsync` → `_tcp.RemoveExpiredAsync`
with the `prunePending` hook, gated on the 60 s `_interval`), not the UDP tick — verified by reading
`IdleExpirySweeper.cs:121-136`, `:178-184`.

---

## 5. The per-flow counters that already exist (the classification signal)

**They exist, they are per flow, and reading them is free — but they are not reachable from the
session today.**

- `UdpAssociationEvidence` (`src/WinForward.Runtime/UdpProxy/UdpAssociationCapability.cs:32-55`):
  `private int _datagramsSent` + `private int _sawResponse`; readers `DatagramsSent` (`:38`,
  `Volatile.Read`) and `SawResponse` (`:41`, `Volatile.Read`); writers `RecordDatagramSent()`
  (`:44`, one `Interlocked.Increment` per successfully sent datagram) and `RecordResponseReceived()`
  (`:50-54`, a volatile short-circuit plus a write-once `Interlocked.Exchange`).
- **One evidence object per lease** (`UdpAssociationLease._evidence`, `UdpAssociationLease.cs:16`),
  i.e. **one per flow** — the lease is rented per flow from the association pool and released when the
  flow's transport is disposed. Readers on the lease:
  `RecordDatagramSent()`/`RecordResponseReceived()` (`UdpAssociationLease.cs:52`, `:58`); there is
  **no public/internal read accessor** on the lease today.
- Write sites, all in `Socks5UdpTransport`: send accepted by the kernel on the warm path (`:350`), the
  contended-gate tail (`:372`), the overlapped async fallback (`:385`); first successfully decoded
  relay datagram (`:463`, after the source/oversize/malformed checks at `:454-460`, so a skip records
  nothing). These are the "whole hot-path addition" of the pooling task (`hot-path.md:772-800`).
- Cost to read: `DatagramsSent` and `SawResponse` are each a `Volatile.Read` of an int/flag on an
  object the flow already references — no lock, no clock, no allocation.
- **Reachability:** `UdpProxySession` holds `IUdpProxyTransport _transport` (`UdpProxySession.cs:55`,
  from `UdpProxySessionContext.Transport`, `:24`) and `UdpAssociation Association`
  (`:106`, the *coordinator's* table entry, not the lease). `IUdpProxyTransport`
  (`Socks5UdpTransport.cs:76-91`) exposes only `RelayEndpoint`, `LocalEndpoint`, `SendSpanAsync`,
  `ReceiveAsync` — **no counter surface**. The lease is a private field of the concrete
  `Socks5UdpTransport` (`:191`). So the classification needs a seam (design §4.3); it cannot read the
  evidence through today's transport contract.
- The `PinningSuspicionThreshold = 3` constant (`UdpAssociationCapability.cs:64`) is the sampler's,
  read only on the pool's 5 s maintenance tick (`UdpAssociationPool.SnapshotEvidence` → `Evaluate`,
  `:76-85`) — it is **not** a one-shot/sustained threshold and must not be reused as one (it means
  "3 unanswered sends is suspicious", which is a different question).
- The session's own counters (`_skippedUnexpectedSource` etc., `UdpProxySession.cs:72-76`) count
  *anomalies*, not exchange volume, and are write-only diagnostics.

**Evidence is per session, and that is load-bearing for the classification.** The evidence object is
created by the pool per lease (`UdpAssociationPool.StartLease` → a fresh `UdpAssociationEvidence`) and
removed from the association's live set on release (`ReleaseLeaseAsync`), so a re-established session
for the same flow key starts from `DatagramsSent = 0, SawResponse = false`. Two consequences: (a) a
`SawResponse` observed by a session can only come from **its own** reply (never a previous incarnation's),
so no stale promotion can happen; and (b) a session can never be *early*-classified as one-shot — the
predicate requires a datagram and a response that this session actually handled. The flip side is the
per-session scoping of the sparse-flow cost (design §6 row 3): a keepalive flow never accumulates a
second datagram **within one session**, so it never promotes, and each of its datagrams pays a fresh
session that the short TTL then retires — bounded at one re-establishment per sparse datagram.

**What does not exist, and must not be added:** a session-local datagram counter. Every session send
already pays exactly the one evidence increment the pooling task documented; a second increment in
`SendSpanAsync`'s tail would be a new per-datagram cost, which the PRD forbids
("must not become a new per-datagram cost"). The classification must read the existing evidence.

---

## 6. The retention boundary facts the change moves

Today one `TimeSpan` governs both the pre-filter cutoff and the authoritative re-check, and the
observable contract is:

> an idle session stamped at `t0` is retired on the first sweep tick at or after
> `t0 + idleTimeout + w − (t0 mod w)`, i.e. in `(idleTimeout, idleTimeout + w + sweep]` — **never
> early**, at most one 500 ms bucket plus one tick late.

The facts a two-class retention moves:

1. **The cutoff is no longer a per-sweep constant.** With classes τ_short (5 s) and τ_long
   (30 s configured), a session's cutoff depends on its own class. The pre-filter must use the
   *later* cutoff (`Cutoff(now, min(τ))`) so the candidate set stays a superset, and the
   authoritative `TryBeginExpiry` must recompute the class cutoff under the session gate. The
   "never early" guarantee survives exactly because the re-check is authoritative — the pre-filter was
   already documented as "a pre-filter only" (`UdpProxyCoordinator.cs:462-463`).
2. **The never-early bound becomes class-relative:** `(τ_class, τ_class + w + sweep]`, and **both
   classes' worst-case lateness moves because the tick moves** (the *TTL* is unchanged for the long
   class, its *bound* is not):
   - long class (τ = 30 s): `30 + 0.5 + 15 = 45.5 s` → `30 + 0.5 + 5 = **35.5 s**` — **tightens**;
   - short class (τ = 5 s): `(5, 20.5] s` → `(5, **10.5**] s`.
   The direction matters for the record: no session can be retired later than before, and the only
   sessions retired *earlier* are the completed one-shots by design.
3. **The sweep cadence moves with the floor.** If the one-shot class is 5 s and the tick stays 15 s,
   a one-shot session really lives `(5, 20] s`; the honest reading of "a ~5 s TTL" requires the tick to
   be derived from the *effective minimum* retention (5 s, which is also
   `MinimumUdpSessionIdleSeconds`, `ConfigurationLimits.cs:59` — the shortest retention the 500 ms
   activity quantum was designed for, `ActivityBucket.cs:8-12`). One *other* retention on the same tick
   tightens with it, the F8 pending-entry lifetime `(5, 20] s → (5, 10] s` (§4).
4. **Retirement moves the resident set, not the per-flow cost.** Sessions retired earlier release the
   relay socket (kernel buffer), the receive-window lease, the session objects and the association
   table entry; a flow whose next datagram arrives after its class TTL pays a **fresh setup** (new
   relay socket + session objects; the control connection is normally still warm in the association
   pool, `udp-relay.md:569`). That is the trade §7 of `design.md` prices.
5. **The association-table entry follows the session.** `RemoveSlotAsync` removes the flow's
   `UdpAssociation` and its relay alias under the gate (`UdpProxyCoordinator.cs:520`), so a later
   datagram for the same flow claims a fresh alias — there is no stale reverse route to a closed relay
   socket.
6. **Facts that must NOT move:** the 500 ms bucket quantization, the `<= 0` "retire everything"
   drain meaning, `TryBeginExpiry`'s `_scope.IsIdle` requirement (no in-flight send is cut off), the
   `Expiry` teardown reason and its "no setup cooldown" rule (`:529-532`), and the two-argument
   sweep's uniform behaviour.

---

## 7. Burst / loss scenario anchors that must not change

From `benchmarks/results/2026-09-28-udp-reuse/README.md` (Step 3 `auto` = the shipped default shape;
host NixOS 26.11, .NET 10.0.12, X64 — the same host this task runs on):

| Scenario (command shape) | Recorded anchor | Source |
|---|---|---|
| `--stability --scenario udp --pps 25000 --duration 60` | 1,499,000 sent = received, **loss 0**, 24,982.2 pps, 15 `sendLoopOverflows` | README "Step 3" table |
| `--stability --scenario udpBurst --burst-flows 48 --dial-delay-ms 100 --duration 60` | 48/48 first responses, **establishmentLossRate 0**, p50 305.5 ms, max 613.6 ms, background loss 0 | README "Step 3" table |
| `--stability --scenario churn --burst-flows 48 --churn-waves 0 --duration 120 --socks5-external` | 222,720 sessions, **7,577.5 B/session**, firstResponse p50 3.246 ms, 0 rejected, loss 0 | README "Step 3" table (inside the `hot-path.md:823-830` band ≤5,400 Noop / ≤8,200 framework / ≤14,500 wave / ≤14,300 sustained) |
| `--stability --scenario udpSessionBudget --rate 100 --churn-seconds 90 --drain-seconds 120 --socks5-external --require-pooling` | peak 4,500 sessions ≤ 6,200 ceiling; 1.07 descriptors/session; **500 MiB** estimated kernel buffer (4,000 × 128 KiB); drain to zero at 120.3 s; `retentionBounded`+`poolingCovered` true | README "Measured — post-caps" run D |
| the same at `--rate 5` | peak 225 ≤ 332; 1.065 descriptors/session; 25 MiB estimate (200 × 128 KiB) | run E |
| the 1 h artifact (`--churn-seconds 3600`, recorded 2026-09-29) | 360,000 flows, 0 loss, 4,500 peak, 1.070 descriptors/session, 500 MiB estimate, drain zero at t = 3,630.3 s | README §"1-hour acceptance run" |

The soak's ceiling arithmetic is `rate × (idle + 2 × sweep) + margin` with the **configured** idle and
the **derived** sweep (`UdpSessionBudgetInstrumentation.cs:35-38`; `SteadyStateMargin(rate) =
max(32, 2 × rate)` at `:44-45`), and the scenario derives its own sweep interval from
`IdleExpirySweeper.DeriveUdpSweepInterval` with the configured idle
(`UdpSessionBudgetScenario.cs:57-62`), driving `RemoveExpiredAsync(now, idleTimeout)` itself
(`UdpSessionBudgetRun.cs:206`; the warm-up drain uses `TimeSpan.Zero`, `:97`). Both the derivation
input and the driven call are on this task's change path: a one-shot class must reach the scenario or
the series does not describe the shipped shape.

**Every row the cadence re-base moves** (the amended PRD sanctions the re-base and requires the
ceilings to be re-derived from the **measured** resident set, with the load's meaning restated):

| Row | Before (sweep 15 s) | After (sweep 5 s) |
|---|---|---|
| `SessionBudgetMath.SteadyStateSessionCeiling(100, 30 s, sweep, 200)` (`SteadyStateMargin(rate) = max(32, 2 × rate)`, `UdpSessionBudgetInstrumentation.cs:44-45`) | 100 × 60 + 200 = **6,200** | 100 × 40 + 200 = **4,200** |
| `MinimumChurnSeconds` at rate 100 (`floor(ceiling/rate) + 1`) | 63 (63 × 100 = 6,300 > 6,200) | **43** (43 × 100 = 4,300 > 4,200) |
| the refusal sentence | "raise `--churn-seconds` above 62" | "above **42**" |
| `ThePostCapsAcceptanceShapePassesBothHalves` `:84`/`:86` | `SessionCeiling == 6_200`, `MinimumChurnSeconds == 63` | `4_200`, `43` |
| `ANonDiscriminatingChurnWindowIsRefusedInsteadOfClaimingRetention` `:135`/`:136`/`:139` | `63`, "above 62" ×2 | `43`, "above 42" ×2 |
| `TheDefaultChurnWindowDiscriminatesAtTheDefaultRate` `:150` ceiling at rate 20 | 20 × 60 + 40 = **1,240** | 20 × 40 + 40 = **840** (load 20 × 90 = 1,800 still clears it) |
| `TheRetentionHalfCoversTheEstimatedKernelReceiveBuffer` `:194`/`:201` | `RelayReceiveBufferBudget == 6_200L × buffer`; the inflated row uses `6_200L × buffer + 1` | `4_200L` in both places |
| `AcceptanceLoadSessions` (`:19`, used at `:29-36`, `:44-47`, `:71-76`, `:96-107`, `:125-130`, `:183-206`, `:215-220`) | 4,500 — the **pre-change measured peak**, now **above** the new 4,200 ceiling, so `ThePostCapsAcceptanceShapePassesBothHalves` (`:67`) and `ASaturatedPopulationSkipsThePoolingHalf…` (`:90`) invert | re-derived from the **after-run's measured `steadyStateSessions`** (expected ≈1,000–1,500 at rate 100, the one-shot band), artifact-cited; the rows derived from it (the `ceil(N/16) + 16` association ceiling, the tenth-held-privately row, `SessionsBeyondSharedBudget = N − 256`) move with it |
| the load's *meaning* | "the peak the acceptance load produces at the 30 s retention/15 s sweep shape" | "the peak the acceptance load produces at the shipped two-class retention and 5 s sweep" — the shipped command (`--rate 100 --churn-seconds 90`) stays, and its 9,000 cumulative flows clear 4,200 with 2.1× headroom (was 1.45×), so the window stays discriminating and gets sharper |

`UdpSessionBudgetAcceptanceTests` also hard-codes `relayReceiveBufferBytes: (long)AcceptanceLoadSessions
* 64 * 1_024` in the **misattribution** row (`:215`): with the new default exactly 64 KiB that estimate
becomes *correct*, `SessionBudgetAcceptance.cs:209-211` stops firing and
`Assert.False(misattributed.RetentionBounded)` fails. The row must be re-pointed at a value that is
still misattributed (e.g. 32 KiB), and it belongs on the Step-2 re-base list beside the two `InlineData`
aggregates.

The `udp.sessionFootprint` instrument (`benchmarks/WinForward.Benchmarks/Stability/SessionFootprintScenario.cs`)
runs 1/100/1000 sessions over `BenchmarkUdpTransportFactory` fake transports with
`Capacity = sessions` (`:33`), one local `NativeBufferPool` per run (`:31`, **default capacity 256** —
it does not mirror composition), and reports `workingSetDeltaBytes`, `gen0Collections`,
`allocatedBytes` measured **before disposal** (`:36-49`). It drives **no sweep at all**. Its recorded
rows are old — the newest are
`benchmarks/results/2026-08-29-udp-fix/stability-linux-final.jsonl:4-6`
(`allocatedBytes` 6,888 / 480,936 / 7,090,632 for 1/100/1000; `workingSetDeltaBytes` **0** in all
three) — and they predate five structural tasks, so they are a historical shape only; this task must
record its own before-numbers. Three attribution facts follow directly:

- `workingSetDeltaBytes == 0` at every recorded population, so that column cannot carry any claim.
- the fake transport opens **no socket**, so the kernel-buffer change is invisible to this instrument,
  and a first populate cannot show a pool-capacity change at all (§3).
- because the scenario builds the pool with the **literal default** and the coordinator with
  `Capacity = sessions`, its second-wave overflow growth before the change is `max(0, N − 256)` —
  **+744 at N = 1,000** — and it would still read +744 after the change unless Step 3 also constructs
  the scenario's pool through `ReceiveWindowPoolCapacity(sessions)`; the instrument must mirror the
  composition, with the before-run keeping the literal default so the red exists (`§9/D16`).

---

## 8. Reproduce-before commands and the numbers they must produce

Run on the pre-change tree, in this order, with the artifact directory created first. `--output`
truncates per process, so multi-run series are concatenations of temp files (recorded lesson,
`09-30-expiry-sweep-bounded-pause` disposition 19).

```bash
mkdir -p benchmarks/results/2026-10-01-udp-session-footprint

# 1. managed/native footprint at 1 / 100 / 1000 sessions (fake transports, no sweep)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario footprint \
  --output benchmarks/results/2026-10-01-udp-session-footprint/footprint-before.jsonl

# 2. the retention-bounded soak at the acceptance load (one-shot-dominant population)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpSessionBudget --rate 100 --churn-seconds 90 --drain-seconds 120 \
  --socks5-external --require-pooling \
  --output benchmarks/results/2026-10-01-udp-session-footprint/session-budget-before.jsonl

# 3. the burst/loss anchors (must not move)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udp --pps 25000 --duration 60 \
  --output benchmarks/results/2026-10-01-udp-session-footprint/udp-loss-before.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpBurst --burst-flows 48 --dial-delay-ms 100 --duration 60 \
  --output benchmarks/results/2026-10-01-udp-session-footprint/udp-burst-before.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpChurn --burst-flows 48 --churn-waves 0 --duration 120 --socks5-external \
  --output benchmarks/results/2026-10-01-udp-session-footprint/udp-churn-before.jsonl

# 4. the exact red-before for the pool gate (a test, not a scenario): with the pre-change
#    capacity (256) and a 300-session population cycle, the second cycle's overflow growth is 44
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~UdpReceiveWindowPoolTests"
```

Expected shape of (1) on the current tree: the three `allocatedBytes` values will differ from the
2026-08-29 rows (five structural tasks landed since) and `workingSetDeltaBytes` is expected to be ~0
again; the honest before/after pair for this task is the **within-task** pair, with the 2026-08-29
rows cited only as the historical shape. Expected shape of (2): peak ≈4,500 sessions, ≈500 MiB
kernel estimate, 1.07 descriptors/session, 0 loss, verdict passed. Expected shape of (3): the table in
§7, within the recorded bands.

---

## 9. Recorded discrepancies (code wins)

| # | Source claim | Code reality | Effect |
|---|---|---|---|
| D1 | PRD AC 2: "a session population at that capacity produces **zero** overflow allocations (exact counter gate), where the pre-change composition produced one per session beyond 256" | `OverflowAllocations` counts every rent that misses the free list (`NativeBufferPool.cs:90`), so a first fill of N live sessions increments it N times at *any* capacity; with capacity 256 and a live population above 256 the *per-cycle growth* is `population − 256`, not "one per session beyond 256" of the absolute counter | the gate becomes **zero growth across a second population cycle** (300 sessions: 0 at the new capacity vs +44 at 256); the absolute counter is never asserted to be 0 |
| D2 | PRD "every session beyond the 256th runs on tracked overflow allocations" | true as *churn*: with the free list unable to fill, each session beyond the pooled population allocates and frees one lease per lifetime (`Release` frees when `_inPool >= Capacity`, `:112-117`) | the claim stands qualitatively; the gate and the artifact must state it as growth, not as a per-session absolute |
| D3 | PRD §Problem "~1.6 KiB each" for the receive window | 1,537 B payload + a 4-byte in-band rental-state word = **1,541 B** per buffer (`NativeBufferPool.cs:86-88`, `UdpProxyCoordinator.cs:215-216`) | use 1,541 B in the memory arithmetic; 1.6 KiB is the rounded form |
| D4 | PRD §Problem "at the 16,384-session capacity that projects to ~2 GiB of kernel receive memory" | 16,384 × 128 KiB = 2 GiB exactly, but the *actual* concurrent population is retention-bounded: the 1 h artifact peaked at 4,500 sessions / 500 MiB | state the projection at capacity **and** the measured retention-bounded figure |

| D5 | PRD requirement 1: "the existing capacity×buffer validation still warns on an oversized product" | the capacity warning's sentence embeds `default buffer KiB × capacity / 1024` (`ConfigurationLimits.cs:156`) and two `[InlineData]` rows encode 512 / 2,048 MiB for the default (`ConfigurationLimitsTests.cs:171-176`) | changing the default to 64 KiB mechanically moves those two expected aggregates to 256 / 1,024 MiB — a **required, mechanical test update**, not a weakening (the threshold pair `>256 KiB ∧ >2,048 sessions` is untouched) |
| D6 | PRD §Problem item 3: the parked receive loop's "retention is driven by the idle TTL, which today is uniform" | correct: one `TimeSpan` reaches both the pre-filter and `TryBeginExpiry` (`UdpProxyCoordinator.cs:455`/`:477`, `UdpProxySession.cs:234`), derived from `udpSessionIdleSeconds` only | the classification must be introduced at the session, because only the session owns the evidence |
| D7 | PRD requirement 3: "the classification signal comes from the per-flow datagram counters the capability-evidence path already maintains" | true (`UdpAssociationEvidence.DatagramsSent`/`SawResponse`), but **not reachable from `UdpProxySession`**: `IUdpProxyTransport` exposes no counter (`Socks5UdpTransport.cs:76-91`) and the lease is private to the concrete transport (`:191`); `UdpAssociationLease` has no read accessor (`UdpAssociationLease.cs:52-58` are write-only) | a three-line seam is required (`design.md` §4.3); the PRD's "nearly free" stands (two `Volatile.Read`s off the packet path), the "already reachable" implication does not |
| D8 | PRD §Notes / research F6: "the footprint series shows the resident-set effect" of the TTL | `udp.sessionFootprint` drives **no sweep** (`SessionFootprintScenario.cs:26-50`) and uses fake transports, so neither the TTL change nor the kernel-buffer change can appear in it; the retention-bounded instrument is `udp.sessionBudget` (which drives the sweep itself) | the mixed-population series must be built (design §6): a controlled two-class arm in the footprint scenario **plus** the soak's one-shot re-record |
| D9 | PRD "Recorded instruments: `udp.sessionFootprint` … and the `udp.sessionBudget` soak …, **both** under `benchmarks/results/2026-09-28-udp-reuse/`" | that directory holds only `sessionBudget` rows; the newest `udp.sessionFootprint` rows are `benchmarks/results/2026-08-29-udp-fix/*` (pre-F2…F8) | the 2026-08-29 rows are cited as a historical shape; the evidence pair is captured inside this task |
| D10 | research F6 "pool default capacity 256 — every session beyond 256 is an overflow allocation (`NativeBufferPool.cs:22`)" | line 22 is `private const int DefaultCapacity = 256;` — exact; but it is applied only because `DurableCaptureBundle.cs:233` passes no capacity | the fix site is composition, not the pool type |
| D11 | research F6 "one parked async receive loop (`UdpProxySession.cs:247-300`)" | the loop is `ReceiveLoopAsync` at `:291-344` (the cited range is the pre-F2 file) | anchors updated |
| D12 | `benchmarks/README.md:187-190` documents `udp.sessionFootprint`'s metrics | accurate; the scenario ignores every option and always runs 1/100/1000 (`SessionFootprintScenario.cs:20`) | instrument changes must be additive to that row, or the documented shape changes |
| D13 | the sweeper's `udpSweepInterval` override is documented as "the test seam for driving the UDP cadence" (`IdleExpirySweeper.cs:76`) | also used by `IdleExpirySweeperCadenceTests` (`:23`, `:73`) | fine; a new optional floor parameter must default to `null` so both existing uses are untouched |
| D14 | implicit in the PRD's "idle TTL … today is uniform" | the F8 attribution pending index has its **own** 5 s TTL on the same tick (`FlowAttributionPendingIndex.cs:176`, `IdleExpirySweeper.cs:156-168`) | a tick-period change moves that leg's frequency **and tightens its bound** (`(5, 20] s → (5, 10] s`, a raw wall-clock compare with no bucket term at `:489`); record it (design §6) and update the `error-handling.md:16` row. The TCP pending-SYN TTL is unaffected — it rides the **main** leg, not the UDP tick (`IdleExpirySweeper.cs:178-184`) |

| D15 | first-draft design: "`TryBeginExpiry`'s two-argument form is removed; its only caller is the coordinator" | false — `UdpProxySessionTests.cs:97` calls `session.TryBeginExpiry(time.GetUtcNow(), TimeSpan.Zero)` | the landed shape keeps a **delegating two-argument overload** (`oneShotIdleTimeout: idleTimeout`), which also keeps every F3 caller uniform and needs no test edit (`design.md` §5.2) |
| D16 | first-draft design: the footprint cycle row would read +0 after the change | `SessionFootprintScenario.cs:31` builds its pool with the literal `NativeBufferPool(ReceiveWindowSize(cap))` (default capacity 256) while the coordinator gets `Capacity = sessions`, so a 1,000-session second cycle grows **+744** regardless of the composition change | Step 3 must construct the scenario's pool through `ReceiveWindowPoolCapacity(sessions)`; the Step 1 before-run keeps the literal default so the red exists, and the README states the instrument mirrors composition |
| D17 | first-draft design: the pool capacity is `sessionCapacity`, "headroom deliberately 0" | the retire/admit overlap is unbounded (§3 above) | `ReceiveWindowPoolCapacity(cap) = cap + ReceiveWindowRetireHeadroom(cap)` with a documented allowance (`design.md` §3); the pure rule row asserts `cap + headroom`, the cycle gate is unchanged in shape |
| D18 | first-draft design: "the soak's ceiling arithmetic and every verdict field stay as they are" | with a 5 s sweep the rate-100 ceiling drops to 4,200, which is **below** `AcceptanceLoadSessions` 4,500, so two acceptance rows invert and `MinimumChurnSeconds` moves 63 → 43 | the amended PRD sanctions the re-base; every moved row is enumerated in §7 above and `implement.md` Step 4/5, with the load's meaning restated and `AcceptanceLoadSessions` re-derived from the measured after-peak |
| D19 | first-draft design: the buffer's test blast radius is the two `InlineData` aggregates | `UdpSessionBudgetAcceptanceTests.cs:215` hard-codes a 64 KiB per-session estimate in the **misattribution** row, which becomes the new default and turns the expected failure into a pass | re-point that row at a value that is still misattributed (32 KiB) and add it to the Step-2 re-base list |
| D20 | first-draft design: "≈25.2 MiB", "≈43 max-size responses" | decimal MB vs MiB confusion: 16,384 × 1,541 B = 24.07 MiB (25.6 MiB with the retire allowance), 4,500 × 64 KiB = 281.25 MiB, ≈1,000 × 64 KiB = 62.5 MiB; 65,536 / 1,472 ≈ 44 | every byte figure is quoted in **MiB/GiB** from exact products, and the kernel range is quoted in one unit (500–563 MiB before → 250–281 MiB after at the same peak) |
| D21 | first-draft implement.md: the new three-argument exact gate goes in `SweepAllocationGateTests.cs` | that file is already **508 effective lines** (`directory-structure.md:55-61` caps at 400) | the new gate goes in a **new file** (`UdpAdaptiveSweepAllocationGateTests.cs`), and the stale "every 15 s" prose rows are fixed in the same step: `SweepAllocationGateTests.cs:478`, `udp-relay.md:216`, `udp-relay.md:395`, `SoakOptions.cs:152` |
| D22 | review note, verified correct | the evidence object is per lease, so it is created fresh per session; `TouchActivity` stamps on receives as well as sends; reading the classification outside `_activityGate` is safe because `_scope.IsIdle` plus the bucket cutoff under the gate are authoritative; `OverflowAllocations` is free-list misses only so cycle 1 = N at any capacity and cycle 2 = `max(0, N − cap)`; the seam passes the layering/analyzer check and a coordinator-supplied delegate would not be simpler; `DeriveUdpSweepInterval` returns 5 s when fed the 5 s floor | folded into `design.md` §4.1/§4.2/§4.3/§3 and `research/implementation-notes.md` §4/§5 |

---

## 10. Dead ends and traps (recorded against re-exploration)

- **Do not add a session-local datagram counter.** It would be a new per-datagram cost in
  `SendSpanAsync`; the evidence already exists (§5).
- **Do not reuse `PinningSuspicionThreshold = 3`** as the one-shot threshold — it answers a different
  question (unanswered sends), and its name would make the two rules indistinguishable.
- **Do not route the classification through the association pool's sampler.** It runs on a 5 s
  maintenance tick and reads only *attached* leases; attaching a retention decision to it would couple
  the retention of a session to the placement of its association.
- **Do not sweep one-shot sessions from a second index.** A second population index is per-session
  bookkeeping on the admission path for a scan that is already bounded, zero-allocation, and
  single-hold (`SweepAllocationGateTests`); the F3 shape deliberately has one scan.
- **Do not measure the kernel-buffer change with a managed instrument.** It is a kernel `SO_RCVBUF` on
  a real socket; the only honest statements are the configured value (exact), the applied value on a
  real socket (exact, order-of-magnitude on Linux where the kernel doubles `SO_RCVBUF`), and the
  arithmetic estimate at the measured population (series).
- **Do not re-open the shared-relay-socket design** (`research.md` §"Rejected / dead-end ideas",
  §F6 dead end): the SOCKS5 UDP reply header carries only the peer address, so several client flows to
  one destination cannot be demultiplexed; per-flow relay sockets are a NAT requirement.
- **Do not add a config key for the one-shot TTL in this task.** The PRD asks for a documented
  constant; a key widens the surface and the validation matrix for a policy nobody has measured a
  deployment need for. (The buffer key already exists and is the PRD's only required override.)
- **Do not try to remove the retire/admit overlap by counting retiring sessions in the admission
  gate** (e.g. `_sessions.Count + _retiring >= Capacity`). It would make the pool capacity exact, but
  it turns a transient teardown into a *capacity rejection* for a new flow — trading a native
  allocation for a fail-closed datagram drop, which is the wrong direction for a fail-closed proxy.
- **Do not size the headroom at a full extra capacity** (2× pool = 50 MiB native): the overlap is a
  transient of the retire path, not a second population; the documented allowance plus the recorded
  residual is the honest shape.
