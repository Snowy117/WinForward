# TCP Redirect Teardown Grace and Flow Hold

> What happens to a redirect after it retires: the one atomic retire, the TIME_WAIT tombstone, how late
> packets are consumed, and how a held flow survives idle expiry. Read it when you change
> `TcpRedirectSessionStore`'s teardown/retire/sweep paths, `TcpRedirectTombstoneTable`,
> `TcpRedirectTable`'s removal or resolve paths, `TcpProxyCoordinator.HoldsFlow`, or the idle sweeper's
> leg order. Part of the TCP local-redirect family; the hub
> [tcp-local-redirect.md](./tcp-local-redirect.md) has the pipeline overview, the cross-cutting
> invariants and the topic map. The close a relay end delivers *before* its retire — the crafted RST for
> an abnormal end, the bounded drain of the stack's FIN for a clean one — is
> [tcp-client-close-injection.md](./tcp-client-close-injection.md)'s; the store's quiescence scope and
> dispose ordering are [tcp-relay-lifecycle.md](./tcp-relay-lifecycle.md)'s.

## Signatures

- `TcpRedirectTombstoneTable` (`TcpRedirectTombstoneTable.cs`) — dual-key (`FlowKey` forward + reverse
  `Endpoint` pair) onto a shared entry with `ExpiryUtc`: `TryAdd(forward, reverseSource, reverseDestination,
  expiryUtc)` (FIFO evict-oldest at capacity), `TryHit(FlowKey, now)` and `TryHit(reverseSource,
  reverseDestination, now)` (a hit only while `now < ExpiryUtc`), `RemoveExpired(now)` (also head-drains
  the insertion-order queue).
- `TcpRedirectOutcome.Dropped` (`TcpRedirectInterfaces.cs`) — a dedicated outcome. **Never reuse
  `Blocked` for grace drops**: the executor's `Blocked` branch (`NdisPacketActionExecutor.LogProxyBlocked`)
  reports a proxy problem, which mislabels a normal grace consume as a proxy failure.
- `FlowTable.RemoveExpired(DateTimeOffset now, TimeSpan idleTimeout, Func<FlowKey, bool>? isHeld = null)`
  (`src/WinForward.Core/FlowTable.cs`) — the optional hold predicate; held entries are skipped **without
  `Touch`**, so they expire at their original idle point once the hold lapses.

## Contracts

- **Single tombstone write point.** Every teardown entry — relay completion, relay failure, fail-closed,
  global dispose — funnels through `TcpRedirectSessionStore.RemoveAssociationFromTable`, the only caller
  of `TcpRedirectTable.TryRemove`, and that point writes the tombstone in the same table critical section
  as the removal (grace `s_tombstoneGracePeriod` = 60 s, owned by `TcpRedirectSessionStore`; it covers the
  client's post-retire ACK and common FIN retransmissions without parking entries for a full 240 s TIME_WAIT).
  Any new teardown path must go through it.
- **Atomic retire (task 08-30-atomic-retire, 2026-08-30).** `RetireSessionUnderGate` performs the
  session-dictionary removal, `Phase = Closing`, `Retire()`, the table alias removal **and** the tombstone
  arming inside one store-gate critical section. All three retire entry points — `TearDownSessionAsync`,
  the sweep's `RemoveExpiredAsync`, and `DisposeCoreAsync` — are covered by that one method. Disposal
  (listener → self-traffic token → relay → lifetime CTS) trails *outside* the gate; only the synchronous
  table + tombstone pair is atomic. Documented lock order: **store gate → table gate → tombstone gate**,
  verified acyclic repo-wide — table/tombstone critical sections never call upward, so do not add a path
  that does. Session-less release paths (`FailAssociationAsync`'s no-session branch, `TcpRedirectSetup`'s
  disposed-store path) still call the standalone `RemoveAssociationFromTable`; they are mutually exclusive
  with retire per association instance, and `TryRemove`'s `ReferenceEquals` guard makes any repeat call
  (and any port-bitmap double-decrement) a no-op. The rule this protects: **a same-tuple SYN must never
  reach an about-to-be-disposed listener** — the relay-end close made the retire→removal window
  practically reachable, because the client reacts to the close while the stale alias is still resolvable.
- **The tombstone queue drains on sweep (R3-TCP).** `RemoveExpired` head-drains `_insertionOrder` while the
  head is expired *or* stale (the dictionary's current entry for the key is a different record — a refresh
  appends a fresh tail with a new expiry, so queue order ≈ expiry order and head-drain is order-safe).
  Before this, the queue grew monotonically with total `TryAdd` calls (days of churn → hundreds of MB)
  while the dictionaries stayed bounded.
- **Late-packet consumption.** After a forward (`TryResolveByOriginal`) or reverse (`TryResolveByReverse`)
  miss, the coordinator consults the tombstone before falling back — **on the SYN path too**
  (`HandleSynAsync` probes after the resolve miss and before the capacity gate / `TryClaim`; wired
  2026-08-30 with atomic retire, because a straggler SYN in grace previously opened a fresh redirect
  instead of consuming the grace). A hit returns `Dropped`; the executor silently consumes it (trace
  `packet.dropped reason=grace`), and the dispatcher maps a reverse-straggler `Dropped` to `ProxyConsumed`
  — **not** `Block`, which would emit `reason=policy`.
- **The `NotRelevant → Pass` fallback is solely for connections established before capture started.** Those
  packets are data-plane-identical to late packets, so no timestamp can separate them; only the per-flow
  tombstone expiry can.
- **Flow hold.** `TcpProxyCoordinator.HoldsFlow` = has a session ∨ has a tombstone. The sweeper runs
  **tcp → flows → udp** (`IdleExpirySweeper.SweepMainLegsAsync`), so tombstones and sessions are recycled
  before flows are evaluated, and the capacity summary stays at tick end. Held flows are not touched, so a
  silently-idle relaying flow survives flow-idle expiry and resumes without re-evaluation (no
  `flow.created`). Generation is deliberately not compared: a new flow reusing the tuple claims a new
  table generation, so the hold naturally lapses.
- **Capacity.** The tombstone table is constructed with the same capacity as the session store, which the
  composer wires from `tcpFlowCapacity` (`TcpRedirectComposer` → `TcpRedirectOptions.Capacity`, default
  16 384). Tombstones occupy neither `_sessions` nor the `TryClaim` capacity gate.
- **One reverse probe per packet (task 09-30-warm-path-lock-chain, 2026-09-30).** `TryResolveByReverse`
  *is* the reverse-candidate check: `HandleReverseAsync` takes the resolved `TcpRedirectAssociation` and no
  longer probes, and `HandlePacketAsync` resolves once and passes it through. A warm reverse packet takes
  exactly **one** reverse-index probe and **zero** redirect-gate entries when its reverse entry is
  cache-resident (a cache collision costs the one gated probe). A warm forward packet takes zero probes and
  zero entries **when its local port is not itself a live listener port**, because `HandlePacketAsync`
  keeps the listener-port prefilter in front of the probe: a reverse tuple's source port is always a live
  listener port — the count rises in the same `_gate` hold that publishes the reverse index entry and
  falls in the same hold that removes it — so a prefilter miss *proves* the reverse index cannot match.
  The prefilter is a **candidate filter, not a proof of absence for the forward direction**: client source
  ports and translated listener ports are both OS-assigned ephemerals, so a forward flow whose source port
  number coincides with a live listener port takes one gated reverse probe per packet while that listener
  exists, and the miss then falls through to the original-index probe. The slow path
  (`HandleReverseIfApplicableAsync`) deliberately does **not** prefilter, so a tombstone straggler whose
  port was already decremented still reaches the full check. The two per-packet indexes are served from
  pre-allocated direct-mapped caches (`TryResolveByReverse` by the reverse tuple, `TryResolveByOriginal` by
  the original key) validated by the association's get-only fields — a collision, an unpopulated slot or a
  stale entry can only cost the lock, never a wrong answer. The four `Dictionary` indexes stay the gated
  authority, and every mutation (claim, the factored `RemoveUnderGate`, the sweep) runs under `_gate` and
  maintains the corresponding cache entry under a `ReferenceEquals` guard. `Count` reads
  `_byOriginal.Count` under the gate — there is no maintained counter. `_byAddressPair` stays gated (the
  fragment path is not a warm packet path). Association activity is a bucket stamp: `Touch` writes
  `ActivityBucket.FromUtc(now)`, and both sweeps compare `BucketForDiagnostics <
  ActivityBucket.Cutoff(now, idleTimeout)`.

## Validation & Error Matrix

| Condition | Result |
|---|---|
| Late forward/reverse packet within grace | `Dropped`, silent consume, no reinjection to the real server |
| Late packet after grace expiry | falls back `NotRelevant → Pass` (pre-existing-connection semantics) |
| Tombstone table full | evict the oldest entry, add the new one |
| Relay-held flow reaches flow-idle expiry | skipped, no `Touch`, no re-evaluation on resume |
| Hold lapses (teardown + grace passed) | flow expires at its original idle point |

## Good/Base/Bad

- Good: a post-retire straggler — a retransmitted FIN, or an ACK that missed the clean-end drain — hits
  the tombstone and is consumed, so no RST bounces from the real server.
- Base: a packet for a connection torn down 90 s ago (grace lapsed) passes as `NotRelevant`, the same as
  pre-capture traffic.
- Bad: reusing `Blocked` for grace drops (mislabels a grace consume as proxy-unavailable); holding flows by
  `Touch`ing them (defeats original-idle-point expiry).

## Tests Required

- `TcpRedirectTombstoneTableTests`: window hit, evict-oldest, expiry recycle, rewrite-refresh, and
  queue-drain convergence (`RemoveExpiredDrainsStaleQueueRecordsFromRefreshChurn`,
  `QueueLengthConvergesToLiveEntriesUnderRefreshAndExpiryChurn`).
- Coordinator tests: both-direction straggler `Dropped`; grace-expiry fallback to `NotRelevant`;
  relay-failure writes a tombstone; executor silent consume + trace (and no executor `packet.completed`
  for grace); `HoldsFlow` phases; flow expiry at the original idle point after the hold lapses (split
  clocks); real-sweeper silent-flow survival (no `flow.created`); the atomic-retire parking-listener test
  `RetireRemovesTableAliasAndArmsTombstoneBeforeListenerDisposalCompletes` (a same-tuple SYN mid-teardown
  must hit the tombstone, never the dying listener).
- Dispatcher regression: a reverse-straggler `Dropped` maps to `ProxyConsumed` without a `reason=policy`
  label.
- Sweep gates (task 09-30-expiry-sweep-bounded-pause): `SweepAllocationGateTests`' two redirect sweep
  facts — a retiring tick over 4 096 idle associations and a no-op tick over 64 registered `Redirecting`
  sessions — each an exact `GC.GetAllocatedBytesForCurrentThread` window with a populated world, a pinned
  managed thread id, `IsCompletedSuccessfully` on the async one, and a call-count backstop.
- Warm-path gates: `TcpRedirectWarmPacketTakesZeroGateEntriesAndZeroClockReads` (0 gates, 1 probe),
  `ReverseResolveCompletesWhileRedirectGateIsHeld`, `TcpReversePrefilterTests`.

## Wrong vs Correct

#### Wrong

```csharp
// Grace drop reusing Blocked: the executor's Blocked branch logs a proxy problem
// and the trace says reason=policy — both mislead diagnosis.
if (tombstone.TryHit(key, now)) return TcpRedirectOutcome.Blocked;
```

#### Correct

```csharp
// Dedicated outcome; the executor consumes silently with its own trace reason,
// and the dispatcher maps the reverse-straggler form to ProxyConsumed.
if (Tombstones.TryHit(reverseSource, reverseDestination, now) ||
    Tombstones.TryHit(key, now))
    return TcpRedirectOutcome.Dropped;
```
