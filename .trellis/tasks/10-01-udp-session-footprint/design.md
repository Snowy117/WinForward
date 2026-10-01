# Design — F6 UDP per-session footprint: relay receive buffer, pool sizing, adaptive idle TTL

Scope: three per-session resources on the UDP relay path — the relay socket's kernel receive buffer
(128 KiB default), the shared native receive-window pool (default capacity 256), and the idle TTL that
decides how long a session keeps both. Nothing on the per-datagram path changes: no send, no decode,
no reinjection, no session admission, no teardown protocol. Every mechanism below is either a
composition value, a pool construction argument, or a decision taken on the **idle sweep**, never on
the packet path.

Deliberately not in scope: the shared-relay-socket dead end (recorded against re-exploration in
`research/implementation-notes.md` §10), the relay-socket-per-flow invariant, the F2/F3/F4/F5/F8
mechanisms, the A4 FlowTable rebuild, and the Windows-only measurement rows.

---

## 1. Chosen mechanism per site

| # | Site | Mechanism | Kind of change | Where |
|---|---|---|---|---|
| 1 | Relay socket kernel receive buffer | default **128 KiB → 64 KiB** in both constants; range (16..1024), warning thresholds, and the config override unchanged | two constants + the test rows that encode their arithmetic | `Socks5UdpTransport.cs:174`, `ConfigurationModels.Udp.cs:66` |
| 2 | Native receive-window pool | capacity **derived from the configured session capacity** at composition: one lease per live session **plus a documented retire/admit allowance** (the overlap has no in-code bound) | one pool-construction argument + two named rules | `DurableCaptureBundle.cs:233` |
| 3 | Idle retention | a session whose exchange is **one datagram, already answered** is retired on a **5 s** TTL; everything else keeps the configured TTL (30 s default) | one predicate + one constant in `UdpProxySession` (with a delegating two-argument overload), one overload on the coordinator sweep, one optional sweeper parameter | `UdpProxySession.cs:232-247`, `UdpProxyCoordinator.cs:445`, `IdleExpirySweeper.cs:43-69` |
| 3b | Sweep cadence (consequence of 3) | the UDP tick is derived from the **effective minimum** retention (5 s at the defaults, was 15 s) | one derivation input; `DeriveUdpSweepInterval` itself unchanged | `IdleExpirySweeper.cs:65`, `:78-84` |
| 4 | Evidence seam (enabler of 3) | the flow's existing capability counters are exposed through a small internal interface implemented by the transport | additive interface + two accessors; **no send/receive line touched** | `Socks5UdpTransport.cs:76-91` area, `UdpAssociationLease.cs` |
| 5 | Evidence (measurement) | a **control/treatment** mixed-retention arm (two cohorts, one sweep each at the same instant) + pool-cycle accounting; the soak re-recorded with the shipped cadence | benchmark-only | `benchmarks/WinForward.Benchmarks/Stability/` |

---

## 2. Site 1 — the relay receive buffer default: 64 KiB

**Chosen value: 64 KiB** (`udpRelayReceiveBufferKb: 64`), the conservative end of the research's
32–64 KiB range.

**Trade arithmetic.**

- *What the buffer is for.* Relay responses can arrive faster than the single receive loop decodes and
  reinjects them; the socket buffer absorbs that gap. It is **not** the datagram assembly bound — that
  is the native receive window (`cap + 22 + 1`, `UdpProxyCoordinator.cs:215-216`) and the oversize
  sentinel skip (`Socks5UdpTransport.cs:484`).
- *Absorption at 64 KiB:* 64 KiB / 1,472 B ≈ **44** maximum-size (standard-MTU) responses, or ≈ **128**
  responses at the benchmark's 512-byte payload, queued per socket. At the recorded steady load
  (25 kpps over 256 flows ≈ 98 datagrams/s/flow, `benchmarks/results/2026-09-28-udp-reuse/README.md`)
  64 KiB is ≈ 1.3 s of that flow's arrivals at 512 B (128 queued / 98 per second) — three orders of
  magnitude above the receive
  loop's per-datagram latency (loopback p50 first response 3.2 ms, sustained send p95 ≈ 0.06 ms).
- *Aggregate at the shipped capacity:* 16,384 × 64 KiB = **1 GiB**, from 16,384 × 128 KiB = 2 GiB.
- *Aggregate at the measured population:* the 1 h soak peaked at 4,500 live sessions → **281 MiB**
  (4,500 × 64 KiB = 294,912,000 B), from **500–563 MiB** (4,000–4,500 × 128 KiB); after the adaptive TTL
  the same load's one-shot population is ≈ `rate × (5 s + tick)` ≈ 1,000 → **62.5 MiB**. Both figures
  are `live sessions × configured bytes`, the same product the heartbeat reports (`Cli/Program.cs:316`)
  and the soak asserts — an arithmetic series, not a kernel measurement. Every byte figure in this
  document is MiB/GiB of the exact product, never decimal MB.
- *What 64 KiB costs:* a response burst larger than 64 KiB arriving between two decode passes on one
  socket is dropped **by the kernel**, invisibly — WinForward has no counter for it. The measured
  guards are the loss and burst anchors (§7): `udp` loss 0, `udpBurst` 48/48 with
  `establishmentLossRate 0`, `udpChurn` 0 loss. If any of them moves, the fallback lever is the
  existing config key (`udpRelayReceiveBufferKb: 128` restores today's value byte for byte); the
  design does **not** ship 32 KiB first because its margin against the same anchors is unmeasured and
  the extra 512 MiB at capacity is not needed to satisfy the PRD.

**Everything else stays.** The accepted range 16..1024 KiB, the `>256 KiB ∧ >2048 sessions` warning
pair, and the override semantics are untouched (`ConfigurationLimits.cs:49-55`, `:168-192`). **Three**
test rows encode the *default's* arithmetic and therefore move mechanically: the capacity warning's two
aggregates (`[InlineData(4097, 512)]`, `[InlineData(16384, 2048)]`) and the **misattribution** row in
`UdpSessionBudgetAcceptanceTests.cs:215`, which hard-codes a 64 KiB per-session estimate — exactly the
new default, so its expected failure would become a pass. It is re-pointed at a value that is still
misattributed (32 KiB). A new guard asserts the two constants agree
(`DefaultUdpRelayReceiveBufferKb * 1024 == DefaultRelaySocketReceiveBufferSize`), which nothing does
today.

**Exact proof that the value reaches a socket.** The `AppliedRelayReceiveBufferSize` seam
(`Socks5UdpTransport.cs:238-243`) exists for exactly this and has never been read. The new fact builds
a transport through the production `Socks5UdpTransportFactory` (real loopback socket, `mode: Off`) and
asserts the applied value. On Linux the kernel doubles `SO_RCVBUF`, so the exact claim is on the
*configured* value (`configuration.UdpRelayReceiveBufferBytes`, already pinned by
`UdpProxyCompositionTests.cs:22-48`) and the socket fact asserts `applied >= requested` with the
read-back recorded; on Windows the read-back is the requested value. This split is stated in the
artifact rather than papered over.

---

## 3. Site 2 — the receive-window pool sized from the session capacity

**Rule.** One lease per live session, **plus a documented retire/admit allowance**.

```csharp
// UdpProxyCoordinator, beside ReceiveWindowSize (the other composition-owned window constant)
/// <summary>
/// The receive-window pool's capacity for a composition admitting this many live sessions.
/// One lease per live session, because UdpProxySession rents one window for its whole receive loop
/// (UdpProxySession.cs:294) and returns it in the loop's finally (:334); plus
/// <see cref="ReceiveWindowRetireHeadroom"/>, because a session's slot leaves _sessions before its
/// lease returns: RemoveSlotAsync removes the slot under _gate (UdpProxyCoordinator.cs:512-518) and
/// only then awaits session.DisposeAsync() outside it (:536), so a newly admitted session can rent
/// while a retiring one still holds its window. Capacity is a bound, not a preallocation:
/// NativeBufferPool allocates on demand and only retains what was ever rented, so the allowance
/// costs nothing until it is used and at most headroom x 1,541 B in total.
/// </summary>
internal static int ReceiveWindowPoolCapacity(int sessionCapacity) =>
    checked(sessionCapacity + ReceiveWindowRetireHeadroom(sessionCapacity));

/// <summary>
/// The concurrent-retirement allowance: the expiry retire is serial (one await RemoveSlotAsync per
/// candidate), but receive-failure teardowns are concurrent scope children
/// (UdpProxyCoordinator.cs:546-547) and send-path/setup removals await their own teardown from their
/// own callers, so the overlap has no in-code bound and a simultaneous multi-session fault is the
/// worst case. The allowance is therefore a documented policy number, not a proof: four
/// association-wide fault bursts at the default fan-out, and at least a sixteenth of the capacity so
/// it scales with a raised udpSessionCapacity. In-tree precedent for "size the pool so the steady
/// state cannot overflow": the attribution pool, DurableCaptureBundle.cs:236-238.
/// </summary>
internal static int ReceiveWindowRetireHeadroom(int sessionCapacity) =>
    Math.Max(4 * ConfigurationLoader.DefaultUdpAssociationFlowsPerAssociation, sessionCapacity / 16);
```

**Composition site** (`src/WinForward.Cli/DurableCaptureBundle.cs:233`):

```csharp
var udpWindowPool = new NativeBufferPool(
    UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize),
    UdpProxyCoordinator.ReceiveWindowPoolCapacity(configuration.UdpSessionCapacity));
```

The rules live next to `ReceiveWindowSize` so both composition-owned window constants are one grep
away, and the `udp.receiveWindow` diagnostics registration (`:234`) is unchanged.

**Why the allowance, and its derivation.** The coordinator admits at most
`configuration.UdpSessionCapacity` live sessions (`UdpProxyCoordinator.Send.cs:72`,
`UdpProxyOptions.Capacity` default 16,384), but admission is gated on `_sessions.Count`, not on
outstanding leases: `RemoveSlotAsync` drops the slot under `_gate` and awaits disposal outside it, and
the lease returns only in the receive loop's `finally` (`UdpProxySession.cs:334`). Receive-failure
teardowns are launched per faulting session on the coordinator's scope (`:546-547`) and never take
`_sweepGate`, so a fault storm can have N retirements in flight at once — a capacity of exactly `cap`
would re-enter the tracked-overflow path under exactly the event the pool exists to smooth. The
allowance is `max(4 × default association fan-out (16) = 64, cap / 16)`:

| Capacity | Headroom | Pool capacity | Allowance cost at 1,541 B/lease |
|---|---|---|---|
| 300 (the gate) | 64 | 364 | 96 KiB |
| 16,384 (the ship) | 1,024 | 17,408 | **1.5 MiB** |

The floor covers four association-wide fault bursts (one association death faults up to
`FlowsPerAssociation` attached flows, `udp-relay.md:564`); the 1/16 term keeps the allowance
proportional if an operator raises `udpSessionCapacity`. **Residual, recorded:** a fault storm larger
than the allowance still allocates fresh leases transiently — self-correcting, bounded by the storm, and
never a steady state, which is what the retired "one per session beyond 256" churn was.

**Cost.** Capacity is a bound: nothing is allocated at composition. The pool retains its high-water
mark (a returned lease is enqueued while `_inPool < Capacity`, freed otherwise,
`NativeBufferPool.cs:112-117`), so the worst case is `(16,384 + 1,024) × 1,541 B = 26,825,728 B ≈
**25.6 MiB**` native held after a full-capacity peak (24.1 MiB of that is the population itself),
against the 1 GiB kernel saving from site 1; at the measured populations it is ≈1.5 MiB. The first fill
is unchanged (it allocates N times at any capacity).

**The exact gate — corrected (`research/implementation-notes.md` §9/D1).** `OverflowAllocations`
counts every rent that misses the free list, so the absolute counter starts at N for N live sessions
regardless of capacity and cycle 2 grows by `max(0, N − capacity)`. The discriminator is **growth
across a second population cycle**:

| Shape | Cycle 1 (N = 300 live) | Cycle 2 (same 300, after a full release) | Verdict |
|---|---|---|---|
| pre-change: capacity 256 | `OverflowAllocations == 300` | `== 344` (**+44**) | red-before, recorded |
| post-change: capacity `ReceiveWindowPoolCapacity(300)` = 364 | `== 300` | `== 300` (**+0**) | green |

Two facts carry it: a pool-level one (`NativeBufferPool` at `ReceiveWindowPoolCapacity(300)`, rent
300 / release 300 / rent 300, assert the counter is unchanged and the balance identity
`Rented == Returned`, `Outstanding == 0`, `InPool == 300` holds) and a coordinator-level one (a
coordinator over fake transports at capacity 300, two full population cycles, the pool's overflow
growth asserted 0). The red-before is produced by running the same fact with capacity 256 **before**
the composition change, and the 44 is recorded in the artifact. The pure rule row asserts
`ReceiveWindowPoolCapacity(cap) == cap + ReceiveWindowRetireHeadroom(cap)` — **not** `== cap` — with
the two rows of the table above.

---

## 4. Site 3 — adaptive idle retention

### 4.1 The rule

> A session whose flow has sent **exactly one datagram** and has **already received a response** is a
> *completed one-shot exchange*; it is retained for `OneShotIdleTimeout` (5 s). Every other session
> keeps the configured `udpSessionIdleSeconds` (default 30 s). The classification is monotone:
> `DatagramsSent` only grows and `SawResponse` is write-once, so a session can only leave the one-shot
> class, never enter it late.

`OneShotDatagramThreshold = 1` is a private constant on `UdpProxySession`, the single site that
applies it. Derivation: the one-shot class is *a request/response exchange that is over*. DNS — the
UDP majority the PRD names — is one query datagram and one reply; the reply is counted by
`SawResponse`, not by `DatagramsSent`, so the threshold is 1. A second datagram is positive evidence
that the flow is a stream, and from that point the flow keeps the long TTL for the rest of its life.
`SawResponse` in the conjunction is what keeps a *slow first reply* safe: an unanswered exchange never
enters the short class, so a server answering after 6 s or 20 s still lands (today's 30 s behaviour),
and the short class only ever applies to a flow whose exchange has demonstrably completed and then
gone quiet. Reusing `PinningSuspicionThreshold = 3` (`UdpAssociationCapability.cs:64`) was rejected:
it answers "three unanswered sends" and would conflate two different rules under one name.

**The classification is per session, and that is the honest scoping of the cost.** The evidence object
is created per lease by the pool, so a re-established session for the same flow key starts from
`DatagramsSent = 0`: a `SawResponse` a session sees can only be its own reply (no stale promotion), and
no session can be classified before it has handled a datagram and a response. The flip side is that a
**sparse flow never promotes**: a keepalive that speaks every 10 s has one datagram per session, so it
is classified one-shot, retired at 5 s, and re-established on its next datagram — indefinitely, not
just once. The cost therefore lands on the **per-flow setup path**, bounded at one re-establishment per
sparse datagram, and the measured price of a re-establishment is the churn anchor (≈7.6 KB/session,
`benchmarks/results/2026-09-28-udp-reuse/README.md`) — while a *first* reply that is merely slow is
still covered by the `SawResponse` conjunct (it keeps the long TTL, so slow-but-real exchanges are
never cut).

**The constant.** `UdpProxyCoordinator.OneShotIdleTimeout = TimeSpan.FromSeconds(5)`, public and
static, next to `ReceiveWindowSize`/`ReceiveWindowPoolCapacity`; the sweeper reads it through
composition. 5 s is not arbitrary: it is `MinimumUdpSessionIdleSeconds`
(`ConfigurationLimits.cs:59`), the shortest retention the 500 ms activity quantum was designed for
(`ActivityBucket.cs:8-12`, "at least eight buckets per retention window"), so the short class is the
finest retention the activity representation can honour without changing it. A configured
`udpSessionIdleSeconds` at or below 5 s makes the two classes identical and the session takes
`min(configured, short)`, i.e. uniform retention — the degenerate case, handled by construction.

### 4.2 Where the decision is made and how often

- **Site:** `UdpProxySession.TryBeginExpiry`, under `_activityGate`, once per session per sweep
  **candidate**. Not on the packet path, not on admission, not in the transport.
- **Frequency:** at most once per session per UDP sweep tick — 5 s at the defaults after §4.4 — and
  only for sessions already older than the *short* cutoff (the pre-filter). A live sustained session
  is therefore read at most ~12 times per minute, each read costing two `Volatile.Read`s and one
  `long` comparison.
- **Cost, stated exactly:** the min-cutoff pre-filter admits **every** session idle ≥ 5 s as a
  candidate, so per tick the number of `TryBeginExpiry` calls is bounded by the *idle* population
  (bounded in turn by `rate × (long TTL + 2 × tick)`), and each call takes `_activityGate` once
  (`UdpProxySession.cs:235`) — off the packet path, but not free. It cannot contend with the datagram
  path: `TouchActivity` is lock-free (one `Volatile.Write` plus a CAS-throttled observer,
  `:454-467`), so the warm send/receive path never takes that gate. Reading the predicate itself
  outside the gate is safe because `_scope.IsIdle` and the bucket cutoff, both checked **under** the
  gate, are the authority; the predicate only selects which cutoff is compared.
- **Signal:** `UdpAssociationEvidence.DatagramsSent` and `.SawResponse` — the counters the pooling
  change already maintains (`UdpAssociationCapability.cs:38`, `:41`), incremented once per accepted
  send (`Socks5UdpTransport.cs:350`, `:372`, `:385`) and write-once per first decoded response
  (`:463`). **No new per-datagram work exists anywhere in this change**: the send path and both
  `Record*` methods are byte-identical (the diff must not touch them), and the proof is that plus the
  two existing 0-byte UDP gates staying green (`HotPathAllocationGateTests.EstablishedUdpDatagramPathAllocatesNoManagedBytes`,
  `Socks5UdpTransportSendTests.WarmSyncSendAllocatesNoManagedBytes`) — not a timing measurement.

```csharp
// UdpProxySession
private const int OneShotDatagramThreshold = 1;
private readonly IUdpExchangeCounters? _exchange;   // ctor: context.Transport as IUdpExchangeCounters

private bool IsCompletedOneShotExchange =>
    _exchange is { DatagramsSent: <= OneShotDatagramThreshold, SawResponse: true };

internal bool TryBeginExpiry(DateTimeOffset now, TimeSpan idleTimeout, TimeSpan oneShotIdleTimeout)
{
    var effective = IsCompletedOneShotExchange && oneShotIdleTimeout < idleTimeout ? oneShotIdleTimeout : idleTimeout;
    var cutoffBucket = ActivityBucket.Cutoff(now, effective);
    lock (_activityGate) { /* unchanged body, same cutoff comparison */ }
    ...
}

// Kept as a delegating overload: it has a test caller (UdpProxySessionTests.cs:97 passes
// TimeSpan.Zero to drain a session) and it is what keeps the F3 uniform-retention callers uniform.
internal bool TryBeginExpiry(DateTimeOffset now, TimeSpan idleTimeout) =>
    TryBeginExpiry(now, idleTimeout, idleTimeout);
```

### 4.3 The evidence seam (three additive members, no packet-path line)

`UdpProxySession` cannot reach the evidence today (`research/implementation-notes.md` §9/D7). The
smallest honest seam, placed on the already-sanctioned cross-group edge (the transport seam lives in
`Socks5UdpTransport.cs`, `udp-relay.md:68`):

```csharp
// Socks5UdpTransport.cs, beside IUdpProxyTransport
/// <summary>
/// The per-flow exchange evidence a retention policy reads: the counters the association lease
/// already maintains for the capability sampler. Implemented explicitly by Socks5UdpTransport;
/// a transport that does not implement it is classified as sustained (the retention-safe direction,
/// and what keeps every existing fake-transport sweep fact's behaviour unchanged).
/// </summary>
internal interface IUdpExchangeCounters
{
    int DatagramsSent { get; }
    bool SawResponse { get; }
}

// Socks5UdpTransport
int IUdpExchangeCounters.DatagramsSent => _lease.DatagramsSent;
bool IUdpExchangeCounters.SawResponse => _lease.SawResponse;

// UdpAssociationLease (internal read accessors over the existing evidence object)
internal int DatagramsSent => _evidence.DatagramsSent;
internal bool SawResponse => _evidence.SawResponse;
```

One `as` cast per session in the ctor (cold, allocation-free); a null result means "unclassifiable" →
long TTL. No existing implementer of `IUdpProxyTransport` breaks, because the counter surface is a
*separate* interface.

### 4.4 The sweep shape and the cadence consequence

**Coordinator.** `RemoveExpiredAsync(now, idleTimeout)` keeps its exact meaning (uniform retention) by
delegating to a new overload with `oneShotIdleTimeout: idleTimeout`, so every existing caller — the
F3 exact sweep gate included (`SweepAllocationGateTests.cs:483`) — is behaviourally unchanged. The new
overload:

- pre-filters on `ActivityBucket.Cutoff(now, min(idleTimeout, oneShotIdleTimeout))` — the **later**
  cutoff, so the candidate set remains a superset of both classes (the pre-filter is documented as a
  pre-filter only, `UdpProxyCoordinator.cs:462-463`);
- re-checks authoritatively per candidate through the three-argument `TryBeginExpiry`, which recomputes
  its class cutoff under the session gate. **Never early survives because the re-check is the
  authority**, exactly as today.
- `RemoveExpiredAsync` moves to a new partial file `UdpProxyCoordinator.Sweep.cs` (the coordinator's
  main file is at 392 effective lines of the 400 cap; `UdpProxyCoordinator.Send.cs` is the established
  precedent for a behavior-zero file-size split), so the new logic does not breach the cap.

**Sweeper.** `IdleExpirySweeper` gains one optional parameter `TimeSpan? udpOneShotIdleTimeout = null`
appended to the ctor (both existing construction sites — production and
`IdleExpirySweeperCadenceTests` — are untouched), and:

```csharp
_udpSweepInterval = DeriveUdpSweepInterval(_interval, EffectiveUdpRetentionFloor(_relayIdleTimeout, udpOneShotIdleTimeout), udpSweepInterval);
// SweepUdpLegAsync
_expired = await _udp.RemoveExpiredAsync(now, _relayIdleTimeout, _udpOneShotIdleTimeout ?? _relayIdleTimeout);
```

`DeriveUdpSweepInterval` and its theory rows are unchanged; a new pure-function row covers
`EffectiveUdpRetentionFloor(30 s, 5 s) == 5 s`. Production composition
(`DurableCaptureBundle.cs:300`) passes `UdpProxyCoordinator.OneShotIdleTimeout`.

**Why the cadence moves.** A 5 s class swept on a 15 s tick is really a `(5, 20] s` class; the PRD
asks for "~5 s". Deriving the tick from the effective floor makes it 5 s, so a one-shot session is
retired in `(5, 5 + 0.5 + 5] s` — the same one-bucket-plus-one-tick bound the code documents today,
now applied to the short class. The cost is recorded, not hidden: the UDP leg's scan runs 3× as often
(the F3-bounded, zero-allocation, single-hold scan measured ~0.5–1.3 ms at 16,384 sessions →
duty cycle ≤0.03 %, against <1 % before), and the F8 attribution pending-TTL leg — which rides the
same tick with no cadence of its own (`IdleExpirySweeper.cs:156-168`,
`FlowAttributionPendingIndex.cs:176`) — also runs 3× as often. The rejected alternative (keep 15 s,
accept `(5, 20] s`) is recorded in §10 with its arithmetic; it is a one-line change to the floor input
if the re-basing proves noisier than the win.

---

## 5. Contracts before / after

### 5.1 Public product surface

| Surface | Before | After |
|---|---|---|
| `Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize` | `128 * 1024` | `64 * 1024` |
| `ConfigurationLoader.DefaultUdpRelayReceiveBufferKb` / `…Bytes` | `128` / `131_072` | `64` / `65_536` |
| `udpRelayReceiveBufferKb` accepted range + warning pair | 16..1024 KiB; warn `>256 ∧ >2048` | unchanged |
| `udpSessionIdleSeconds` semantics | one TTL for every session | the **long** TTL for every session except a completed one-shot exchange, which gets 5 s; the long class's *TTL* is unchanged but its worst-case retirement age tightens (below) |
| `UdpProxyCoordinator.RemoveExpiredAsync(now, idleTimeout)` | the sweep | unchanged meaning (uniform); delegates to the new overload |
| `UdpProxyCoordinator.RemoveExpiredAsync(now, idleTimeout, oneShotIdleTimeout)` | — | new public overload |
| `UdpProxyCoordinator.OneShotIdleTimeout` | — | new public static `TimeSpan` = 5 s |
| `UdpProxyCoordinator.ReceiveWindowPoolCapacity(int)` / `ReceiveWindowRetireHeadroom(int)` | — | new internal static rules (capacity = session capacity + allowance) |
| `NativeBufferPool` type, its defaults, its `Stats` | — | unchanged (`DefaultCapacity = 256` stays; composition now always passes a capacity) |
| `IdleExpirySweeper` ctor | 11 parameters | + `TimeSpan? udpOneShotIdleTimeout = null` appended |
| `IUdpProxyTransport` / `IUdpProxyTransportFactory` | — | unchanged (the counter surface is a new sibling interface) |
| Config keys | 5 UDP keys | unchanged (no new key) |

### 5.2 Internal surface

- `IUdpExchangeCounters` (internal, `Socks5UdpTransport.cs`) — implemented explicitly by
  `Socks5UdpTransport`; consumed by `UdpProxySession` through one `as` cast per session.
- `UdpAssociationLease.DatagramsSent` / `.SawResponse` (internal read accessors).
- `UdpProxySession.TryBeginExpiry(now, idleTimeout, oneShotIdleTimeout)` (internal) **with the
  two-argument form kept as a delegating overload** — it has a test caller
  (`UdpProxySessionTests.cs:97` passes `TimeSpan.Zero` to drain a session), and keeping it is what
  makes every F3 caller uniform and needs no test edit.
- `IdleExpirySweeper.EffectiveUdpRetentionFloor(relayIdleTimeout, oneShotIdleTimeout)` (internal
  static, pure).
- `UdpProxySessionContext` — **unchanged** (still a value type; the transport it already carries is
  the evidence carrier; `UdpProxySessionTests.SessionContextStaysAValueTypeSoConstructionDoesNotAllocate`
  stays green).

### 5.3 Explicitly preserved

- The 500 ms activity quantum, the "at most one bucket late" property, and the `<= 0` "retire
  everything on this call" drain meaning (`ActivityBucket.cs:13-19`, `:48-49`).
- **Both classes' worst-case retirement age, stated with its direction** (the *TTLs* are unchanged
  except for the new short class; the *bounds* move because the tick moves):
  - long class (30 s): `30 + 0.5 + 15 = 45.5 s` → `30 + 0.5 + 5 = **35.5 s**` — **tightens**;
  - short class (5 s): `(5, **10.5**] s`, where a 15 s tick would have made it `(5, 20.5] s`;
  - the F8 attribution pending-entry lifetime (its own 5 s TTL, same tick, a raw wall-clock compare
    with no bucket term at `FlowAttributionPendingIndex.cs:489`) — `(5, 20] s` → `(5, **10**] s`,
    **tightens**. The TCP pending-SYN TTL is **not** affected: it rides the main-leg sweep
    (`IdleExpirySweeper.cs:178-184`), not the UDP tick.
  No session or pending entry can be retired later than before; only completed one-shots retire
  earlier, by design.
- `TryBeginExpiry`'s `_scope.IsIdle` requirement (no in-flight send is cut off) and the cancel-outside-
  the-gate rule.
- The `Expiry` teardown reason, its "no setup cooldown" semantics, and the association-table removal
  that keeps reverse routing exact (`UdpProxyCoordinator.cs:516-536`).
- The receive loop's lease lifetime (rent once at `:294`, release once in the `finally` at `:334`) and
  the whole `async-lifetime.md` scope contract for `UdpProxySession` — this task changes neither, so
  `async-lifetime.md` needs no edit (checked: it names `UdpProxySession` only in the per-owner table,
  `:348`).
- The two-argument sweep's behavior, and therefore every F3 sweep gate.

---

## 6. Semantics and risk

| # | Change | What it does | Why it is acceptable / what guards it |
|---|---|---|---|
| 1 | **A late reply to a completed one-shot** (after `5 s + bucket + tick`) | the relay socket is already closed; the response is dropped by the kernel (ICMP port-unreachable toward the relay) and WinForward never sees it. The client's retransmission resolves the same flow key, starts a fresh session, and is answered | the retention contract is *never early relative to the flow's class TTL*, not "never late for a late datagram"; UDP is best-effort. Because of the `SawResponse` conjunct, the only response that can arrive after the socket closes is a duplicate or unsolicited extra reply to an exchange that was **already answered** — a slow *first* reply keeps the long TTL and is never at risk |
| 2 | **Never-early for sustained flows** | a session that has sent ≥2 datagrams, or that has not yet been answered, keeps the configured TTL; the counters are monotone so a promotion is permanent | the classifier can only make retention *longer* over a session's life except for the one documented completed-exchange transition; the boundary facts test both edges |
| 3 | **Sparse flows in the `(5 s, 30 s]` gap band with ≤1 datagram** | they are re-established per datagram, **for the flow's whole life** (each new session restarts at `DatagramsSent = 0`, so a keepalive never promotes — design §4.1) | accepted policy change: it is the exact meaning of "a short TTL for one-datagram exchanges". The cost lands on the per-flow setup path, bounded at one re-establishment per sparse datagram, and measured by the churn anchor (≈7.6 KB/session). The fail-open direction is covered by the `SawResponse` conjunct: a *slow first reply* keeps the long TTL and is never cut |
| 4 | **Sweep cadence 15 s → 5 s** | the UDP leg scans 3× as often; the F8 attribution TTL leg rides the same tick and also runs 3× as often, and **its own pending-entry lifetime tightens `(5, 20] s → (5, 10] s`** | the UDP leg is F3-bounded, single-hold, exactly 0 B on a no-op tick; its measured scan hold (~0.5–1.3 ms at 16,384 sessions) is ≤0.03 % duty. Every retention bound tightens, none loosens; the F8 scenario is re-run as a must-not-move companion and the `error-handling.md:16` row is updated |
| 5 | **Relay buffer 128 → 64 KiB** | halves the kernel estimate at any population; reduces burst absorption to ≈44 max-size responses per socket | the recorded loss/burst anchors are re-run (§7); the config override restores 128 KiB exactly; WinForward cannot count kernel drops, so the loss/burst series is the only instrument that can refute it |
| 6 | **Pool capacity = session capacity + retire allowance** | removes per-session native alloc/free beyond the pooled population; retains a high-water mark instead of freeing it | allowance `max(64, cap/16)`: 1.5 MiB at the shipped capacity, 96 KiB at the gate's 300. A fault storm larger than the allowance still allocates fresh, transiently and self-correctingly — the recorded residual, and the reason the allowance exists rather than a capacity of exactly `cap` |
| 7 | **A foreign/fake `IUdpProxyTransport`** | is classified as sustained (no `IUdpExchangeCounters`) | retention-safe default; it is also what keeps every existing fake-transport sweep fact unchanged |
| 8 | **Degenerate configuration** (`udpSessionIdleSeconds ≤ 5 s`) | the two classes collapse (the session takes `min`) | uniform retention, identical to today |

---

## 7. Acceptance mapping — what is exact, what is a series, and why

Every criterion is assigned the *strongest* instrument that can actually attribute it. The recurring
trap (`hot-path.md:908-960`, "Measurement self-checks") is a metric dominated by something other than
the change; the attribution column states what would have to be true for the proof to be vacuous.

| PRD criterion | Exact proof (count / assertion) | Series (recorded, quoted with its control) | Attribution check |
|---|---|---|---|
| **AC1 buffer default** | `DefaultUdpRelayReceiveBufferKb * 1024 == DefaultRelaySocketReceiveBufferSize` (new guard); `ConfigurationDefaultsUdpBudgetWhenOmitted`; the explicit-override rows (512 KiB and **128 KiB** restore); `UdpProxyCompositionTests` carries the value distinctly; the new real-socket fact through `Socks5UdpTransportFactory` (`applied >= requested`, read-back recorded); the capacity-warning rows with their re-based aggregates | the soak's `relayReceiveBufferBytes` / `…PerSession` rows before/after, and the artifact README's projection table (16,384 × 64 KiB = 1 GiB; the measured population's figures) | the socket fact and the composition facts are the proof; the estimate is arithmetic on a configured value and is labelled as such — it must never be presented as a kernel measurement |
| **AC2 pool sizing** | pool-level cycle fact (`ReceiveWindowPoolCapacity(300)` = 364: counter unchanged across cycle 2, balance identity holds) + coordinator-level cycle fact over fake transports; the pure rule row asserts `cap + ReceiveWindowRetireHeadroom(cap)` (364 / 17,408); **red-before** at capacity 256 (recorded +44) | the footprint scenario's cycle row (`overflowFirstWave` / `overflowSecondWave`), with the scenario's pool constructed through the same rule so it mirrors composition | the first fill is *not* the signal (it is N at any capacity); only the second-cycle growth (`max(0, N − capacity)`) discriminates. An absolute `== 0` assertion would be wrong and is explicitly forbidden |
| **AC3 adaptive TTL — boundaries** | `ACompletedSingleExchangeIsRetiredAtTheShortTtlAndNotOneBucketEarlier` (0 removed at exactly 5 s, 1 removed one bucket later); `ASustainedExchangeKeepsTheLongTtl` (0 at 5 s + n buckets, 1 at 30 s + one bucket); `AnUnansweredSingleExchangeIsNotRetiredOnTheShortTtl`; `TheClassificationPromotesOnTheSecondDatagramAndNeverDemotes`; `TheTwoArgumentSweepKeepsUniformRetention`; the sweeper-leg end-to-end fact | — | the facts drive the **real** `RemoveExpiredAsync` + `TryBeginExpiry` path with a mutable clock and a fake transport that implements the evidence interface; the threshold is read from the same constant the product uses |
| **AC3 adaptive TTL — resident set** | — | `udp.sessionRetention` (two identically built cohorts of N one-shot + N sustained, one shared clock past the short TTL, one sweep per cohort at that instant: control with equal TTLs, treatment with the 5 s class) **and** the soak re-record at `--rate 100`, whose measured peak moves from the 4,500 / 6,200-ceiling shape to the one-shot band | the mixed arm's control and treatment differ in **nothing but the classification**, so that series is exactly attributed; the soak's drop bounds the TTL **and** the cadence together, and the artifact says so. The soak's own ceiling and load are re-based from the **measured** resident set (§8), never from the old hand constant |
| **AC3 classification cost** | the diff touches no send/receive/`Record*` line (stated in the artifact and checkable by grep); `HotPathAllocationGateTests.EstablishedUdpDatagramPathAllocatesNoManagedBytes` and `Socks5UdpTransportSendTests.WarmSyncSendAllocatesNoManagedBytes` stay exactly 0 B | `SessionSetupDecompositionBenchmarks` / churn rows as report-only companions | no timing assertion is used for "no new cost" — only "no new code on the path" plus the existing exact gates. The new work is **sweep-side** and stated: one `_activityGate` entry per idle candidate per tick (`UdpProxySession.cs:235`), bounded by the idle population, while `TouchActivity` stays lock-free so the datagram path gains no contention |
| **AC4 semantics preserved** | the existing expiry/teardown/reinjection suites (unchanged), the new preserved-behaviour facts (§5.3), `IdleExpirySweeperCadenceTests` untouched, `SweepAllocationGateTests` (three UDP-area gates) unchanged and green, plus the new exact gate for the three-argument tick | `udpLoss` / `udpBurst` / `udpChurn` shape anchors vs their recorded bands (`research/implementation-notes.md` §7) | the burst/loss anchors are series compared against recorded bands; a movement inside one band is not a result, a movement across bands stops the step |
| **AC5 gates** | Release build zero-warning; full suite green; `dotnet format --severity info --verify-no-changes` empty; `jb inspectcode` zero `<Issue>`; the per-gate process proof for the new/edited exact gates | — | the per-gate proof follows `hot-path.md:1176-1310` (own process, N runs, totals string, signature predicate) |
| **AC6 recorded and cited** | — | the artifact directory + README (`benchmarks/results/2026-10-01-udp-session-footprint/`) with the before/after tables, the red-before, the projection table, the attribution rules, and the residual risks | every number in the README cites the file it came from; the 2026-08-29 footprint rows are cited only as a historical shape |

**Exact counts are never asserted on a first fill, a timing, or a resident level; and no series is
used where a count exists.**

---

## 8. Measurement plan

Artifact directory: `benchmarks/results/2026-10-01-udp-session-footprint/`.

**Before (pre-change tree).** `footprint-before.jsonl` (`--scenario footprint`),
`session-budget-before.jsonl` (the acceptance soak at
`--rate 100 --churn-seconds 90 --drain-seconds 120 --socks5-external --require-pooling`), and the
three must-not-move series (`udp-loss-before`, `udp-burst-before`, `udp-churn-before`). The pool gate's
red-before is a **test** run against the pre-change capacity, not a scenario.

**After (landed tree).** The same set with `-after`, three runs each for the series (the runner
truncates `--output` per process, so multi-run series are concatenated temp files — recorded lesson),
one run per exact count.

**The mixed-retention arm has no before-artifact, by construction.** It measures a mechanism the
pre-change tree does not have; its own **control** is the baseline. The run builds two identically
constructed cohorts (each N one-shot + N sustained), advances one shared clock past the short TTL and
below the long one, and sweeps **one cohort per arm at that same instant**: the control with both
timeouts equal to the long TTL (nothing retires — both classes fully resident), the treatment with the
5 s class (exactly the one-shot class retires). That is a stronger control than a pre-change artifact
would be, because the two rows share every input except the classification — so a treatment movement
without the control standing still is a red flag rather than a result.

**Instruments that change (additive to the documented rows).**

- `Stability/UdpSessionRetentionScenario.cs` (new, `--scenario retention`): the two-cohort
  control/treatment arm described above, one row per cohort carrying the resident counts per class and
  the TTLs it drove; the run aborts unless each cohort's population is proven live before its sweep
  (the fake factory's created count cross-checked against the coordinator's session count) and the
  treatment's two TTLs differ. It is registered in `SoakOptions.ParseScenario` and
  `SoakRunner.SelectScenarios`, excluded from `--scenario all` (it is a probe, not a soak, like
  `scaling`/`sweep`/`residency`/`pump`), documented in `benchmarks/README.md`, and covered by a
  scenario-selection test (`hot-path.md:955-960`).
- `SessionFootprintScenario.cs`: the existing 1/100/1000 row keeps its documented fields
  (`workingSetDeltaBytes`, `gen0Collections`, `allocatedBytes`) and gains a **second population cycle**
  after it, emitting `udp.sessionFootprint.cycle` with `{ sessions, overflowFirstWave, overflowSecondWave }`
  from the shared pool's `Stats`. **The scenario must construct its pool through
  `ReceiveWindowPoolCapacity(sessions)`** (today it passes no capacity, so it would read +744 at 1,000
  sessions and contradict the exact gate); the Step 1 before-run keeps the literal default so the red
  exists, and the README states that the instrument mirrors composition. After the change the
  second-wave figure is the pool claim's series; before it, it is the red.
- `UdpSessionBudgetScenario.cs`: the derived sweep interval passes the effective floor (production
  cadence), and the driven call passes both timeouts. The ceiling keeps its *form*
  (`rate × (idle + 2 × sweep) + margin`, now 4,200 at rate 100) but the load's meaning and every row
  derived from it are re-based — the enumerated list is in §7 of `research/implementation-notes.md`
  and in `implement.md` Step 4/5: the ceiling (`:84`, 6,200 → 4,200), `MinimumChurnSeconds` 63 → 43
  and its refusal sentence (`:86`, `:135`, `:136`, `:139`), the default-rate ceiling 1,240 → 840
  (`:150`), the two `6_200L` budget literals → `4_200L` (`:194`, `:201`), `AcceptanceLoadSessions`
  (from the measured after-peak) with the rows derived from it, and the misattributed-buffer row
  (`:215`, re-pointed to 32 KiB). The
  re-base happens in two stages so no commit is red: Step 4 moves the ceilings and uses a
  bound-derived load, and Step 5 replaces that with the **measured** after-peak quoted from the
  artifact row, re-running the arithmetic rows.
  move with the shipped cadence and the **re-basing is stated in the artifact README** (this is a
  re-base with a reason, not a weakened anchor — the discrimination precondition
  `rate × churnSeconds > ceiling` is re-checked at the new ceiling).

**Projection table to record verbatim in the README** (buffers, not a measurement; all MiB/GiB):
16,384 × 128 KiB = 2 GiB → 16,384 × 64 KiB = 1 GiB; the 4,500-session measured peak 563 MiB → 281 MiB
(500 MiB → 250 MiB at the soak's 4,000-session sampled row); the post-TTL one-shot population ≈1,000 →
62.5 MiB; receive-window pool high-water `(16,384 + 1,024) × 1,541 B = 25.6 MiB` (from an unbounded
alloc/free churn beyond 256).

---

## 9. Rollback shape

Each step is independently revertible; every commit leaves a green tree.

| Step | Revert | Effect |
|---|---|---|
| 1 (instruments) | delete the additive `udp.sessionFootprint.cycle` row + the before-artifacts | nothing product-visible; the before-artifacts are discarded |
| 2 (buffer default) — rollback A | restore the two constants to 128 KiB; restore the two warning aggregates and the misattribution row's 64 KiB literal; drop the new socket/default facts | one-line config revert; no other site depends on the value (all consumers read the configuration) |
| 3 (pool sizing) — rollback B | restore `new NativeBufferPool(ReceiveWindowSize(cap))` at `DurableCaptureBundle.cs:233` and the literal default in the footprint scenario; drop `ReceiveWindowPoolCapacity`/`ReceiveWindowRetireHeadroom` and their facts | returns to the tracked overflow churn; nothing else reads the capacity |
| 4 (seam + adaptive TTL, one commit) — rollback C | revert the three accessors/interface, `TryBeginExpiry`, the coordinator overload (and the partial-file split), the sweeper parameter, the retention scenario and the soak cadence/acceptance re-base; **or**, cheaper, pass `udpOneShotIdleTimeout: null` at composition (one line) | the parameter is the kill switch: with it null the sweeper passes `idleTimeout` for both and derives the old cadence, so the whole TTL change is inert while the seam stays (the acceptance re-base must then be reverted with it, since the ceilings follow the cadence). The seam and its consumer land in **one** commit precisely so the seam is never a dead internal member (which the `jb inspectcode` gate can legitimately flag) |

The pool and buffer changes are independent of the TTL change and of each other; the TTL change is
independent of the buffer change. No rollback requires touching a test's assertion semantics — only
the rows whose expected numbers are *derived from* the changed constants.

---

## 10. Rejected alternatives (recorded)

1. **A session-local datagram counter** instead of the transport seam: a new `Interlocked` on the send
   tail, forbidden by the PRD and by the pooling task's "one increment is the whole hot-path addition"
   contract.
2. **Classify in the sweeper from the association table's `LastActivityUtc`** instead of the session's
   evidence: the table entry is touched at 500 ms granularity and carries no exchange count; it would
   add a second lookup per candidate and answer a weaker question.
3. **A second population index for short-TTL sessions**: per-session bookkeeping on the admission path
   to speed up a scan that is already bounded, single-hold and zero-allocation.
4. **Keep the 15 s tick and accept a `(5, 20] s` one-shot class**: one line simpler and it leaves the
   soak's ceiling arithmetic alone, but at `--rate 100` it keeps ≈1,250 one-shot sessions resident
   against ≈750 at the 5 s tick — 40 % of the TTL win traded for avoiding a cadence re-base. Kept as
   the documented fallback lever (§4.4), not the shipped shape.
5. **A config key for the one-shot TTL**: widens the validated surface for a policy with no measured
   deployment need; the constant plus the session's `min` already covers the degenerate configuration.
6. **32 KiB buffer default**: the research's lower bound; rejected as the first step because its margin
   against the recorded burst/loss anchors is unmeasured and the extra 512 MiB at capacity is not
   required to satisfy the PRD. The key makes it a one-line experiment if a deployment wants it.
7. **A capacity of exactly the session capacity** (no retire allowance): the retire/admit overlap has
   no in-code bound (concurrent receive-failure teardowns never take `_sweepGate`), so the pool would
   re-enter the tracked-overflow path under a fault storm — the event it exists to smooth. Replaced by
   the documented allowance (§3).
8. **Removing the overlap instead of allowing for it** — counting retiring sessions in the admission
   gate (`_sessions.Count + _retiring >= Capacity`): makes the pool capacity exact but converts a
   transient teardown into a **capacity rejection** for a new flow, trading a native allocation for a
   fail-closed datagram drop. Wrong direction for a fail-closed proxy.
9. **Sizing the allowance at a full extra capacity** (2× pool = 50 MiB native): the overlap is a
   transient of the retire path, not a second population.
10. **A capacity-sized headroom derived from a measurement that does not exist**: no artifact records
    concurrent in-flight retirements, and a fault storm is exactly the event a soak cannot schedule
    deterministically. The allowance is therefore a documented policy number with a stated residual,
    not a claimed bound.
