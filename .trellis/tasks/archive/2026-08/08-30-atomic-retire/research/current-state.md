# Research: Atomic retire+remove+tombstone — current state at HEAD 0596a74

- **Query**: Re-validate backlog findings R2 / R3 / R4 (baseline e5667af) against current source; gather design inputs for the fix plan
- **Scope**: internal (src + tests + git history e5667af..0596a74)
- **Date**: 2026-08-30

## Verdict summary

| Finding | Verdict | One-liner |
|---|---|---|
| R2 (two-lock retire/removal window, TCP) | **CONFIRMED** (scope refined) | The retire→table-removal gap persists; table-removal→tombstone was *already atomic* at baseline via `TryRemove(onRemoved)` |
| R3-TCP (tombstone queue growth) | **CONFIRMED** | `_insertionOrder` queue never drained on refresh or expiry; only at-capacity eviction dequeues |
| R3-UDP (`_setupTombstones` unbounded) | **CONFIRMED** | No capacity check; 1s-TTL entries pruned only by the 60s sweep (plus lazy removal on touch) |
| R4 (UDP setup aggregate memory, no datagram TTL) | **CONFIRMED** | 16384 × 32KB = 512 MiB worst case; 8-way limiter; 30s × 4 connect attempts; no timestamps on queued datagrams |

---

## R2 — two-lock retire/removal window (TCP)

### Current teardown path at HEAD

`src/WinForward.Runtime/TcpRedirect/TcpRedirectSessionStore.cs` (file unchanged since e5667af):

1. **`TearDownSessionAsync(session)`** L201–209: takes the **store gate** (`_gate`, L26) → `TryRetireSessionUnderGate` L211–215 (guards: dictionary contains the key AND `ReferenceEquals(current, session)`) → `RetireSessionUnderGate` L217–225, which under the store gate does, in order:
   - `_sessions.Remove(...)` L219
   - `Association.Phase = RelayPhase.Closing` L220
   - `session.Retire()` L221 (cancels the lifetime CTS via `Interlocked.Exchange`, `TcpProxyCoordinator.cs` L422–425)
   - detaches `session.Relay` L222–223
2. Store gate released; **`ReleaseRetiredAsync(retired)`** L227–245 runs *outside* the store gate:
   - `ReleaseAssociationAsync(listener, association, selfTrafficToken)` L266–278:
     - **`RemoveAssociationFromTable(association)`** L293–296 — `_table.TryRemove(association, removed => _tombstones.TryAdd(...))`. The table gate is taken here (after the store gate was already released). `onRemoved` runs *inside* the table gate (`TcpRedirectTable.cs` L288), so **table removal + tombstone arming are one atomic step**.
     - `listener.DisposeAsync()` L271
     - `selfTrafficToken.Dispose()` L276
   - `retired.Relay.DisposeAsync()` L241
   - `session.DisposeLifetime()` L244

**Exact ordering**: session-dict removal → Phase=Closing → retire CTS → relay detach (all under store gate) ⟶ *gap* ⟶ table removal + tombstone (table gate, atomic pair) → listener dispose → self-traffic token dispose → relay dispose → lifetime CTS dispose.

### The race window (still present)

Between step 1 (store gate released) and `RemoveAssociationFromTable` in step 2, the association is **still fully resolvable in `TcpRedirectTable`**, while the session is already retired (lifetime CTS cancelled → accept loop is exiting). A same-tuple packet in this window is honored by every reuse path, none of which consults `Phase == Closing`:

- `TcpProxyCoordinator.HandleSynAsync` L112: `_table.TryResolveByOriginal(key, ...)` → `ReinjectExistingFlowDataAsync` L150–189 — rewrites the SYN toward the listener and injects. The stack completes the handshake with a listener that is disposed µs–ms later → client sees connect-then-instant-death instead of the tombstone's grace-drop.
- `TcpProxyCoordinator.HandlePacketAsync` L328 (mid-flow data): same `TryResolveByOriginal` → same `ReinjectExistingFlowDataAsync`.
- `TcpRedirectTable.TryClaim` L169–174: existing key → `Touch` + return existing; no Phase check (matters if the tuple re-claims after removal but before tombstone consult paths — see below).
- Reverse leg: `HandleReverseIfApplicableAsync` L284 `IsReverseCandidate` → `HandleReverseAsync`; also the X1 warm prefilter `IsReverseCandidatePort` (`TcpRedirectTable.cs` L239) keeps reading nonzero until `TryRemove` decrements L287. After table removal the tombstone hit at L289 covers reverse stragglers, and the dispatcher slow path still runs the full handler (`FlowDispatcher.cs` L134 warm-entry fall-through + L227 `HandleReverseIfApplicableAsync`), so the fall-through theorem holds — the *only* uncovered interval is the retire→removal gap.

Post-gap, `HandlePacketAsync` L338 (`_store.Tombstones.TryHit`) drops forward stragglers for the 60s grace (`TombstoneGracePeriod`, SessionStore L38).

### Fix feasibility check (proposed: move `_table.TryRemove(assoc, onRemoved: TryAdd)` into `RetireSessionUnderGate`/`TryRetireSessionUnderGate`)

- **TryRemove shape at HEAD** (`TcpRedirectTable.cs` L276–291, modified by hot-path-revival): sync; table gate; dictionary removals; `RemoveAddressPairUnderGate` L315–320; **`Interlocked.Decrement(ref _candidatePorts[...])` L287** (new since baseline — X1 port bitmap); `onRemoved?.Invoke` L288; `ReferenceEquals` guard L280 makes repeat calls no-ops (also protects the port count from double-decrement). All synchronous, non-blocking — **safe to call while holding the store gate**.
- **Lock order introduced**: store gate → table gate → tombstone gate (TryAdd takes the tombstone gate inside `onRemoved`).
- **Reverse-order audit (no cycles found)**:
  - Table-gate holders (`TryClaim` L167, `TryResolveBy*` L214/L241/L251, `IsReverseCandidate` L228, `TryRemove` L278, `RemoveExpired` L295, `Snapshot` L312, `Count` L153) never call into the store or tombstones.
  - Tombstone-gate holders (`TryAdd` L51, `TryHit` L67/L74, `RemoveExpired` L80) never call the table or store.
  - Today the store **never** holds its gate across a table call (RemoveAssociationFromTable is only reached outside the gate) — the fix *introduces* the nesting rather than closing an existing cycle.
- **All call sites at HEAD** (verified by repo-wide grep):
  - `_table.TryRemove` — exactly one: `TcpRedirectSessionStore.cs` L295. (UDP's `_associations.TryRemove` in `UdpProxyCoordinator.cs` L313/L427 is the separate UDP table.)
  - `_tombstones.TryAdd` — exactly one: the `onRemoved` lambda on that same line.
- **Paths the fix must keep working**:
  - `RetireSessionUnderGate` is invoked under the store gate from three places: `TearDownSessionAsync` L206, `RemoveExpiredAsync` L121 (sweep), `DisposeCoreAsync` L183 (dispose) — moving TryRemove into it covers all three automatically.
  - **Session-less removal must stay**: `FailAssociationAsync` else-branch L263 (`RemoveAssociationFromTable` when no session exists — reached from `ClientResetInjector.HandleInjectionFailureAsync` L165 and `HandleFragmentTeardownAsync` L128) and `TcpRedirectSetup` L135 (`ReleaseAssociationAsync` when `TryRegister` returned null because the store was disposed). These still need the standalone `RemoveAssociationFromTable`.
  - Listener/relay/token disposal is **async** (`DisposeAsync`) and cannot move under the gate — the fix only moves the *synchronous* table+tombstone pair.
- **Perf consideration**: the table gate is taken per packet on the slow path (`TryResolveByOriginal`/`ByReverse`); the X1 prefilter (`IsReverseCandidatePort`) is lock-free and unaffected. Nesting store→table lengthens table-gate waits only during teardown critical sections (a few dict ops + one Interlocked + tombstone TryAdd) — short and sync.

### Interplay with the newly landed client-RST-on-relay-end (08-30-fast-hardening)

`TcpRedirectAcceptor.ObserveRelayCompletionAsync` L152–194: relay `Completion` awaited → if `relay is ITcpRelayEndInfo { EndKind: not RelayEndKind.CleanEnded }` (L176; `RelayEndKind` = CleanEnded/Stalled/Faulted, `TcpProxyRelay.cs` L66–71) → `TryInjectClientResetAsync` L180 — **RST injection happens BEFORE `_tearDownSession`** (L188), i.e. before retire and before table removal. Two consequences:

1. The RST is crafted from association data (`ClientResetInjector` L50–78 uses the SYN template + sequence trackers), which is unaffected by where `TryRemove` runs — no conflict with the proposed fix.
2. The client can react to the RST (retransmit, or reconnect reusing the tuple) *while the table still holds the association*, making a landing in the retire→removal gap **more likely** than at baseline. The fix closes exactly this.

---

## R3-TCP — tombstone table insertion-order queue growth

`TcpRedirectTombstoneTable.cs` (unchanged since e5667af):

- `_insertionOrder` `Queue<TombstoneEntry>` L23.
- `TryAdd` L48–62: refresh path removes the old entry from both dictionaries (`RemoveEntryUnderGate`, L53–54) but **never dequeues the old queue record**; then enqueues a fresh record L60. Net: every refresh leaks one stale queue record.
- `RemoveExpired` L78–86: removes expired entries from the dictionaries only — **queue records linger**.
- Only `EvictOldestUnderGate` L88–98 dequeues, and it runs only when `_byForward.Count >= _capacity` (L55), skipping stale records via `ReferenceEquals` (L94).
- Sweep cadence: `RemoveExpired` is called every sweep tick from `TcpRedirectSessionStore.RemoveExpiredAsync` L134 (60s default, `IdleExpirySweeper.cs` L44).

**Net effect**: dictionary count is bounded by capacity (default 16_384, from the session budget via ctor L44 of the store), but the queue grows monotonically with **total TryAdd calls** between at-capacity evictions. A churny workload (steady teardowns, never 16k concurrent tombstones) accumulates stale records forever (~130–150 B each: FlowKey + ReverseTuple(2×Endpoint) + DateTimeOffset + header).

**Reference approach** (`TcpResetCooldownTable.cs`): `TryClaim` L34–50 — refresh goes through the `else` branch `_expiryByTuple[tuple] = now + window` L46 **without enqueueing**; enqueue only on `TryAdd` success (new key) L39–42; dict entries are removed only by at-capacity eviction, so queue and dict stay commensurate and jointly bounded.

### Helpful invariant for the fix

Entries are enqueued at TryAdd time with `expiry = now + 60s`, and a refresh appends a *new tail record* with a *new* expiry — so **queue order remains ≈ expiry order** (monotonic). A `RemoveExpired` that dequeues from the head while `(head is stale) || (head expiry <= now)` is order-safe. (A `LinkedList`-based O(1) removal on refresh/expiry is the alternative.)

---

## R3-UDP — `_setupTombstones` unbounded dictionary

`src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs` (unchanged since e5667af):

- Field: `Dictionary<FlowKey, DateTimeOffset> _setupTombstones = []` L23 — **no capacity check anywhere**.
- **Writers**:
  - `RemoveSlotAsync` L431–434 (under coordinator `_gate`): `_setupTombstones[flow] = now + SetupFailureCooldown` on every non-shutdown setup failure; `SetupFailureCooldown = 1s` L16.
- **Readers/removers**:
  - `TrySendAsync` L101–110: cooldown consult; **lazily removes** the entry once elapsed when the flow is touched again (self-healing for active keys).
  - `PruneExpiredTombstones` L392–403: full scan under the gate; called only from `RemoveExpiredAsync` L367.
  - `DisposeCoreAsync` L246: `Clear()`.
- Sweep cadence: `IdleExpirySweeper` default interval **1 minute** (`IdleExpirySweeper.cs` L44), calls `_udp.RemoveExpiredAsync` L76.

**Net effect**: between sweeps, tombstone count is bounded only by (unique failed-flow rate × 60s); entries whose flow is never touched again wait for the sweep. There is no knob for the tombstone budget; contrast with the TCP side's bounded tables.

---

## R4 — UDP setup aggregate memory + no datagram TTL

### BoundedSetupQueue — `src/WinForward.Core/PacketRuntime.cs` L147–219 (unchanged since baseline)

- Dual bounds: `maxPackets` AND `maxBytes`; reject if `frame.Length > _maxBytes || Count >= _maxPackets || _bytes > _maxBytes - frame.Length` L172.
- **Per-packet copy**: `frame.ToArray()` L173 (needed — the source lives in the pump's native batch slot).
- First datagram inline (`_pending`), `Queue<>` materializes on the second L174–192.
- **No drop-oldest inside the queue** — the *caller* implements it: `UdpProxyCoordinator.EnqueueSetupDatagram` L177–192 (`while (!enqueued && TryDequeue(out _))` then retry + `NoteSetupQueueDrop`).
- **No timestamps**: entries are bare `ReadOnlyMemory<byte>` (L152); `TrySendAsync` receives no per-packet wall-clock either (params L91: flow/server/payload/ct/packetSequence/flowGeneration/clientMac; grep for `Timestamp` in `WinForward.Core` → no packet-timestamp fields). A TTL requires **adding** a per-entry timestamp (entry-shape change to a public Core class).

### UdpProxyCoordinator capacity/concurrency (unchanged since baseline)

- `SetupQueueMaximumPackets = 32` L13; `SetupQueueMaximumBytes = 32_768` L14; `MaximumConcurrentSetups = 8` L15 (`SemaphoreSlim` L26); session `capacity` default **16_384** L46, enforced at slot creation L114 (`_sessions.Count >= _capacity` → reject).
- Setup tasks start immediately (`Task.Run` L129) and park on `_setupLimiter.WaitAsync` L280 ("patient admission") — **their setup queues accept datagrams the whole time they are queued**.
- `FlushSetupQueueAsync` L335–354: takes the coordinator gate **per drained datagram** (L340), sends outside the gate (L352); queue-empty check + `slot.Ready = true` share one critical section L345–349.
- **Worst case math**: 16_384 slots × 32 KB = **512 MiB** of buffered payloads; drain rate = 8 concurrent setups × (~30s DNS + 4×30s connect ≈ 150s worst) → 16384/8 × 150s ≈ **3.4 days** to fully fail. "Held for hours" is conservative — confirmed.
- Where a global budget could hook in:
  - Admission/enqueue: `TrySendAsync` L112–131 (slot creation + capacity check) and `EnqueueSetupDatagram` L177–192 (per-enqueue budget check).
  - Release: `RemoveSlotAsync` L429–430 (drained bytes must be credited back) and `FlushSetupQueueAsync` (bytes leave the pending budget as they are sent).

### Socks5ControlConnection — `src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs`

- `MaxConnectionAttempts = 4` L14; `ConnectAttemptTimeout = 30s` L15 — **unchanged** (the only baseline→HEAD delta in this file is +3 lines: the `NoDelay` X4 bit, L128–130). The single-attempt deadline spans connect + auth (L92–94, L119–127); DNS resolution also bounded by the same 30s (L316–338).

---

## Tests inventory

### TCP redirect lifecycle / teardown (`tests/WinForward.Core.Tests/TcpProxyCoordinatorLifecycleTests.cs`)

| Test | Pins |
|---|---|
| `RelaySetupFailureBlocksAndReleasesAlias` L36 | fail-closed teardown releases the table alias |
| `RelaySetupFailureInjectsClientResetWhenSequencesKnown` L60 (+ L96/L129/L172 forwarded & sequence-coverage variants) | client RST on relay-setup failure, in-window ack values |
| `ExistingFlowInjectionFailureReleasesAssociation` L213 | fail path removes association + tombstones |
| `CancelledRetransmitDoesNotRetireSharedRedirect` L232 / `CancellationAfterSynDoesNotStopSharedAcceptLoop` L252 | cancellation must not tear down a shared redirect — **ordering-sensitive to any teardown change** |
| `ShutdownDisposesAllSessionsAndListeners` L275 / `DisposeWaitsForInFlightSetupAndReleasesLateListener` L293 | dispose-path teardown ordering |
| `UnrelatedAcceptedConnectionIsClosedBeforeRelaySetup` L314 / `AcceptLoopDoesNotRunAwayOnTransientAcceptError` L335 | accept-loop behavior |

### TCP tombstone/grace behavior (`TcpProxyCoordinatorCapacityTests.cs`)

| Test | Pins |
|---|---|
| `LateForwardPacketAfterTeardownIsDroppedWithinGrace` L155 / `LateReversePacketAfterTeardownIsDroppedWithinGrace` L185 | the grace-drop the R2 fix must preserve |
| `LatePacketFallsBackToNotRelevantAfterGraceExpiry` L238 | tombstone expiry restores fall-through |
| `RelaySetupFailureTombstonesTheFlow` L265 / `ExecutorSilentlyConsumesTombstoneHitWithTraceAndNoReinjection` L286 | tombstone write point + drop semantics |
| `HoldsFlowTracksSessionAndGraceWindow` L325 / `HeldFlowExpiresAtOriginalIdlePointAfterGraceLapses` L358 | sweep hold predicate (store + tombstone) |
| `CapacityBoundBlocksFailClosed` L22 / `CapacityRejectionIsCountedTracedAndSummarizedAtInfoLevel` L43 / `OmittedCapacityKeepsLegacyDefaultBudget` L73 / capacity-RST tests L384/L429/L457 | capacity gate + S4 cooldown |

### TCP concurrency/expiry (`TcpProxyCoordinatorConcurrencyTests.cs`)

`SynClaimIsExactlyOnce` L21, `ConcurrentSynBurstOnOneFlowUsesOneListener` L96, `ConcurrentSynBurstWithAsyncListenerStaysExactlyOnce` L117 (exactly-once claim — **the R2 fix must keep these green**), `ExpiryRacingRelayEstablishmentCannotAttachDetachedRelay` L192, `RemoveExpiredAsyncExpiresOnlyRedirectingNotRelayingSessions` L164, `ExpiryRemovesIdleAssociations` L146.

### Relay-end RST (new, 08-30-fast-hardening) (`TcpRelayEndResetTests.cs`)

`FaultedRelayEndInjectsInWindowClientResetBeforeTeardown` L28, `StalledRelayEndInjectsClientResetBeforeTeardown` L45, `CleanRelayEndDoesNotInjectClientReset` L62, `RelayWithoutEndInfoIsTreatedAsCleanEnd` L79, `RelayEndKind*` L97/L115/L129 — **pin the RST-before-teardown ordering** that widens exposure to the R2 window.

### Tombstone table unit tests (`TcpRedirectTombstoneTableTests.cs`)

`BothKeysHitOnlyInsideTheGraceWindow` L14, `FullTableEvictsTheOldestEntry` L31, `RemoveExpiredReclaimsOnlyElapsedEntries` L46, `ReAddingTheSameKeysRefreshesTheExpiry` L61 — **pin current eviction/expiry/refresh semantics; an R3-TCP queue-drain change must keep these green** (they don't observe queue length, so internal restructuring is safe).

### UDP setup queue / tombstones / sweep

| File | Tests | Pins |
|---|---|---|
| `UdpSetupQueueTests.cs` | `FirstDatagramDoesNotAwaitAStalledSetupAndDatagramsRelayInFifoOrder` L24, `SetupQueueOverflowDropsOldestAndDeliversRetainedDatagramsInFifoOrder` L54, `DatagramsCannotBypassTheSetupQueueAroundTheFlush` L90 | FIFO + drop-oldest + no-bypass (flush critical section) |
| same | `FailedSetupEntersCooldownAndRetriesAfterOneSecond` L122 | **the 1s `_setupTombstones` cooldown** |
| same | `SetupConcurrencyCapQueuesFlowsBeyondTheCapUntilASlotFrees` L145, `SetupFlashCrowdOfDistinctFlowsQueuesThroughTheCapWithoutLoss` L179, `DisposeDropsDatagramsStillQueuedForSetup` L227 | 8-way limiter queueing, flash-crowd retention, dispose drain |
| `UdpProxyCoordinatorLifecycleTests.cs` | `RemoveExpiredDisposesIdleSessionAndReleasesAssociation` L24, `FailedSetupReleasesSlotAndCapacityForOtherFlows` L62, `RetiredAssociationCannotRemoveOrRefreshReplacementForSameFlow` L196, expiry-vs-activity races L225/L261 | slot lifecycle + sweep |
| `UdpProxyCoordinatorTests.cs` | burst/transport-identity tests L18–L188, `ReceiveBufferIsBoundedAndReturnedWhenCoordinatorStops` L188 | transport multiplexing, receive-buffer pooling |

---

## Drift from baseline (e5667af → 0596a74)

10 commits; relevant landed code = 08-30-fast-hardening (b0e0c6d) + 08-30-hot-path-revival (795cc1f).

| File | Delta | Impact on findings |
|---|---|---|
| `TcpRedirectTable.cs` | +21 (X1 `_candidatePorts[65_536]` bitmap L140; increment in `TryClaim` L199; decrement in `TryRemove` L287 and `RemoveExpired` L304; lock-free `IsReverseCandidatePort` L239) | **TryRemove now does an `Interlocked` port decrement** — analyzed above, safe under the store gate; the decrement's `ReferenceEquals` idempotence note (L285–286) matters if TryRemove is ever called twice for one association |
| `TcpRedirectAcceptor.cs` | +44 (`ObserveRelayCompletionAsync` RST-on-non-clean-end L152–194) | RST now precedes teardown → widens practical exposure to the R2 gap (see R2 section) |
| `TcpProxyCoordinator.cs` | +16 (`WantsPacket` X1 prefilter L261–265) | warm-path diversion; fall-through theorem keeps tombstone grace intact on the slow path (`FlowDispatcher.cs` L134, L227) |
| `TcpProxyRelay.cs` | +136 (`RelayEndKind` L66–71, `ITcpRelayEndInfo`, end-kind classification) | consumed by acceptor; no direct impact on R2–R4 state structures |
| `Socks5ControlConnection.cs` | +3 (`NoDelay` L128–130) | R4 numbers unchanged |
| **Unchanged since baseline** | `TcpRedirectSessionStore.cs`, `TcpRedirectTombstoneTable.cs`, `TcpResetCooldownTable.cs`, `UdpProxyCoordinator.cs`, `PacketRuntime.cs` (BoundedSetupQueue), `IdleExpirySweeper.cs` | R2's store/tombstone code and all of R3/R4 state are byte-identical to e5667af |

**Correction to the backlog wording**: `TryRemove(onRemoved)` with the tombstone `TryAdd` inside the table gate **already existed at e5667af** (verified: `git show e5667af:...TcpRedirectTable.cs` L259/L268). So "table gate (+ tombstone gate) afterwards" was already one atomic step at baseline; the genuine remaining window is only **retire (store gate) → table removal**, which is what the proposed fix closes.

---

## Design inputs (decision points for the fix plan)

- **Where TryRemove must be called from**: inside `RetireSessionUnderGate` (store gate) so `TearDownSessionAsync`, the sweep (`RemoveExpiredAsync`), and `DisposeCoreAsync` are all covered in one place; the session-less paths (`FailAssociationAsync` else-branch, `TcpRedirectSetup` disposed-store path) must keep calling `RemoveAssociationFromTable` directly. Decide whether `ReleaseAssociationAsync` keeps its `RemoveAssociationFromTable` call (needed for session-less) or the store grows a dedicated session-less release entry point.
- **Disposal stays outside the gate**: listener/relay/token `DisposeAsync` are async and cannot move under the store gate — the fix moves only the sync table+tombstone pair; document the resulting invariant "session-dict removal, table removal, and tombstone arming are one atomic step; listener disposal trails".
- **Lock order**: adopt store → table → tombstone as the documented order (verified acyclic today; no reverse acquisition exists). Note the added table-gate hold time during teardown vs the per-packet slow-path `TryResolve*` gate waits; the X1 prefilter is unaffected (lock-free).
- **Reuse-path guards (optional belt-and-suspenders)**: whether `HandleSynAsync`/`HandlePacketAsync`'s `TryResolveByOriginal` reuse and/or `TryClaim`'s existing-key return should also consult `Phase == Closing` — with the atomic fix this becomes redundant for the retire gap, but a stale Closing association could still be observable between relay-end and `TearDownSessionAsync` (RST-before-teardown path); decide if that residual window warrants the Phase check too.
- **R3-TCP queue-drain predicate**: chosen mechanism — (a) head-drain in `RemoveExpired` while `(head stale) || (head.ExpiryUtc <= now)` (order-safe: refresh appends a new tail record with new expiry, so queue order ≈ expiry order), (b) `LinkedList` with O(1) removal on refresh/expiry, or (c) periodic compaction. Keep `TcpRedirectTombstoneTableTests` green (they don't observe the queue).
- **R3-UDP bound**: capacity for `_setupTombstones` mirroring the TCP bounded tables (evict-oldest at capacity) vs. more frequent pruning; note `TrySendAsync` L109 already lazily removes elapsed entries on touch, so only never-touched flows depend on the sweep. Config knob or constant?
- **R4 global budget**: hook point(s) — admission (`TrySendAsync` slot creation L114 / `EnqueueSetupDatagram` L177) and release (`RemoveSlotAsync` drain L429, `FlushSetupQueueAsync` send); decide global-pending-bytes budget vs. pending-setup-flow count vs. both; drop semantics on budget exhaustion (reject like capacity? tombstone like setup failure?).
- **R4 datagram TTL**: requires adding a per-entry timestamp — `BoundedSetupQueue` is a **public Core class** (entry shape change; FIFO/drop-oldest pinned by `UdpSetupQueueTests`); decide where timestamps come from (no wall clock currently reaches `TryEnqueue`; `UdpProxyCoordinator` has `_timeProvider` to pass in) and the TTL value vs `SetupFailureCooldown`.
- **Config knobs today**: none for any of these — `SetupQueueMaximumPackets/Bytes`, `MaximumConcurrentSetups`, `SetupFailureCooldown` are private consts in `UdpProxyCoordinator`; `TombstoneGracePeriod` (60s) is a private const in the store; `capacity` (16_384) is a ctor param on both coordinators; `IdleExpirySweeper` interval/timeouts are ctor params (1min/5min/5min/2min defaults). Any new bound needs a knob decision (const vs config).

## Caveats / Not Found

- No existing test observes the retire→table-removal gap directly (the grace tests exercise the post-teardown state after `TearDownSessionAsync` completes), so the R2 fix has no red-to-green test today — one should be part of the plan.
- Queue-entry memory size (~130–150 B) is an estimate from field shapes, not measured.
- .NET `System.Threading.Lock` reentrancy semantics were not load-bearing for any conclusion (no same-thread nested acquisition identified on these paths).
