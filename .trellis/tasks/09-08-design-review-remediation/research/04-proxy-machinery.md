# Design Review — TcpRedirect/ + UdpProxy/ + Socks5/ + WinForward.Protocols

> Sub-agent report (explore), 2026-09-08. Read-only review; no files modified.
> Effective-line rule: ≤400 (directory-structure.md:57). **Only one violator exists:
> `UdpProxyCoordinator.cs` at 471 effective lines** — everything else is compliant
> (next highest: TcpProxyCoordinator 319, Socks5ControlConnection 276).

## 1. Module map (type → interface surface → depth verdict)

### TcpRedirect (`src/WinForward.Runtime/TcpRedirect/`)

| Type | Eff. lines | Interface | Verdict |
|---|---|---|---|
| `TcpProxyCoordinator` | 319 | 7 public methods + 6 internal diagnostics | **Deep.** Pure entry routing (SYN/reverse/fragment/data phase selection); all mechanics delegated. Best-shaped module in the repo. |
| `TcpRedirectTable` | 219 | TryClaim + 4 resolvers + TryRemove + expiry | **Deep.** Exactly-once arbiter with 4 indexes + X1 port refcount; single-gate discipline perfect. |
| `TcpRedirectSessionStore` | 230 | register/retire/attach/tear/expire/dispose + setup drain | **Deep.** Atomic retire (R2) + single tombstone write point; the load-bearing invariant holder. |
| `TcpPendingSynSetupIndex` | 192 | retain/attach/complete/expiry/drain + 4 counters | **Deep.** Leaf lock, exactly-once byte budget, mirrors UDP queue bounds. |
| `TcpProxyRelay`(+Factory) | 231 | `EstablishAsync` / `Completion`+`EndKind` | Medium-deep; the one leaky seam lives here (see Issues #2). |
| `TcpRedirectSetup` / `TcpRedirectAcceptor` / `ClientResetInjector` | 133/149/106 | 1–5 methods each | Deep; clean single-responsibility slices (pipeline / accept-drain / failure surface). |
| `TcpRedirectTombstoneTable` / `TcpResetCooldownTable` | 90/43 | TryAdd/TryHit/RemoveExpired; TryClaim | Deep micro-modules; bounded, evict-oldest, queue-drain convergence. |
| `TcpFrameRewriter` / `TcpSequenceObservation` / `TcpRedirectLogging` / `TcpRelayFaultObserver` | 42/50/25/21 | static pure functions | Appropriately shallow — pure function clusters. |

### UdpProxy

| Type | Eff. lines | Interface | Verdict |
|---|---|---|---|
| `UdpProxyCoordinator` | **471** | `TrySendAsync` + `RemoveExpiredAsync` + `DisposeAsync` + 5 diagnostics | **Deep but bloated.** Greatest functionality-to-interface ratio in the repo, yet six concerns fused in one class/file (see §3). |
| `UdpProxySession` | 272 | Start/Send/TryBeginExpiry/Dispose | Deep. Receive loop, skip taxonomy, two-tier activity accounting all internalized. |
| `UdpResponseReinjector` | 193 | `IUdpResponseSink.InjectAsync` (1 method) | **Very deep.** Direction matrix, MAC policy, fail-closed drops, 5 rate-limited loggers behind one call. |
| `UdpAssociationTable` | 141 | TryClaim/TryFind×2/TryTouch/TryRemove×2/expiry | Deep; exactly-once with `created` out-flag. |
| `UdpAdapterTargetSource` | 34 | Host/Resolve + Update | Deep; immutable-snapshot swap, lock-free reads. |

### Socks5 / Protocols

| Type | Eff. lines | Verdict |
|---|---|---|
| `Socks5ControlConnection` | 276 | Deep. Dialing/auth/command state machine; 3 injectable seams (`ConnectAsync` params `resolveAddresses`/`socketFactory`/`onSocketReady`, lines 47–51). |
| `Socks5UdpTransport` (+enum+struct+2 ifaces+factory, 1 file) | 247 | Deep. Sync-warm send path, skip-classification, 3 internal CreateAsync seams (lines 158–161). File packs 6 public types — borderline but under limit. |
| `PacketChecksums` | 270 | Pure; only WinForward.Core referenced (csproj verified). Incremental RFC-1624 rewrite + internal full-recompute oracle (`TryRewriteTcpEndpointsFullRecompute`, line 176) pinning equivalence. |
| `IPFragment`/`IPTcpUdpPacket`/`IPUdpPacket`/`Socks5Udp`/`Socks5State`/`TcpResetBuilder`/`UdpFrameBuilder` | 69–158 | All pure static span codecs, zero runtime state, zero allocation on parse paths. |

## 2. Coordinator symmetry (UDP vs TCP)

Both satisfy the spec's shape (quality-guidelines.md:17–18): flow-keyed dict + `_gate` + `_shutdown` CTS + capacity + single-flight `DisposeAsync` (UDP: `UdpProxyCoordinator.cs:286–315`; TCP: via store, `TcpRedirectSessionStore.cs:154–174`). TryClaim exactly-once, fail-closed teardown, and no-await-on-setup (R8/R1) hold on both.

**Concrete drifts:**

1. **Decomposition** — TCP was split 1158→5 modules; UDP never was. TCP's coordinator holds references to `TcpRedirectSetup/SessionStore/Acceptor/ClientResetInjector` (`TcpProxyCoordinator.cs:30–33`); UDP inlines the equivalents as private methods. This is the root cause of the 471-line violation.
2. **Concurrent-racer handling** — TCP: loser detection + fallback re-inject + `ConcurrentLoserCount` instrumentation (`TcpRedirectSetup.cs:100–105`). UDP: racers are absorbed structurally (slot creation under `_gate`, `UdpProxyCoordinator.cs:155–174` — second racer sees the slot and enqueues); the alias-collision path throws IOException→cooldown (line 392) with **no loser counter**. The spec's counter requirement (quality-guidelines.md:20) is only half-mirrored; acceptable because the race window doesn't exist, but the collision path is uninstrumented.
3. **Setup admission** — TCP: entry cap 1024 + 1 MiB budget + 5 s TTL, *no concurrency limiter* (Task.Run per SYN; `TcpPendingSynSetup.cs:36–43`). UDP: per-flow queue (32 pkt/32 KiB) + 8 MiB global budget + **8-wide semaphore limiter** + TTL re-stamped at dial start (`UdpProxyCoordinator.cs:365–373, 431–442`). Asymmetric patience strategy — both defensible (TCP setups are sub-ms binds; UDP dials do network I/O), but undocumented as a deliberate asymmetry.
4. **Capacity rejection surface** — TCP: RST|ACK + per-tuple cooldown + `CapacityRejectionCount`/`LogCapacitySummary` (`TcpProxyCoordinator.cs:140–149, 88–98`). UDP: `false` + trace only (line 157–161). UDP has no equivalent aggregate counter.
5. **"Tombstone" semantics collide** — TCP `Tombstones` = 60 s TIME_WAIT grace (`TcpRedirectSessionStore.cs:46`); UDP `_setupTombstones` = 1 s setup-failure cooldown (`UdpProxyCoordinator.cs:33`). Same word, different contract; TCP's cooldown equivalent is named `_setupCooldowns` in `TcpPendingSynSetupIndex`. Naming drift, not a bug.
6. **Expiry model** — TCP exempts `Relaying` sessions from wall-clock sweep (M4, `TcpRedirectSessionStore.cs:135–137`); UDP expires by activity with `TryBeginExpiry` CAS + `CancelExpiry` compensation (`UdpProxySession.cs:135–148`). Both correct for their protocol; no shared abstraction attempted — rightly so.

**Which is deeper?** UDP per Ousterhout ratio (3-method interface, enormous functionality) — but TCP demonstrates that the same functionality decomposes into five independently-readable modules. TCP is the better-engineered *shape*; UDP is the deeper *module* and the worse *file*.

## 3. UdpProxyCoordinator split proposal (TCP precedent applied)

Four seams, ordered by extraction safety (each is a mechanical line-range move; test totals must stay 463+ per quality-guidelines.md:26):

**A. `UdpSetupCooldownTable` (~60 eff. lines)** — lines 33, 40, 52-area fields, 108–112, 144–153, 524–554, 586–595. The cooldown dictionary + `PruneExpiredTombstones` + `EvictOldestSetupTombstoneUnderGate`. Structurally identical to `TcpResetCooldownTable`/`TcpPendingSynSetupIndex._setupCooldowns` (bounded, evict-oldest, timestamp-as-age). Make it a **leaf lock** like its TCP counterparts; call sites in `TrySendAsync`/`RemoveSlotAsync` drop out of the coordinator gate. Also rename away from "tombstone" (fixes drift #5).

**B. `UdpSetupQueueBudget` (~65 eff. lines)** — lines 11–34 (constants), 52–58, 114–124 (diagnostics), 222–284 (`EnqueueSetupDatagram`, `TryChargePendingSetupBytes`, `CreditPendingSetupBytes`, `NoteSetupQueueDrop`). Pure Interlocked accounting + rate-limited drop logging; owns the R4 charge/credit exactly-once contract. No gate interaction — trivially extractable.

**C. `UdpSessionSetup` (~150 eff. lines)** — lines 43, 365–422 (`CreateSessionAsync` + `_setupLimiter`), 431–486 (`RefreshSetupStampsAtDialStart`, `FlushSetupQueueAsync`). The dial/claim/construct/flush pipeline — the exact analog of `TcpRedirectSetup`. Receives store-callbacks as ctor delegates exactly as `TcpRedirectAcceptor` does (`TcpRedirectAcceptor.cs:15–16`), so no cycle. Owns the 2026-09-06 dial-start re-stamp fix as one cohesive unit.

**D. `UdpProxyLogging` (~40 eff. lines)** — lines 603–645 (`LogDebug`, `LogTrace`, `LogSetupFailure`). Mirror of `TcpRedirectLogging.cs`.

**Result**: coordinator retains `TrySendAsync` fast path, `SendOnReadySessionAsync`, `RemoveSlotAsync`, `RemoveExpiredAsync`, dispose, and the `UdpSessionSlot` class ≈ **~250 eff. lines** — compliant, and each concern (admission / budget / dial / cooldown / logging) becomes independently testable and greppable. Optionally `RemoveSlotAsync` + `RemoveExpiredAsync` + dispose could later form a `UdpSessionStore`, but A–D alone clears the spec with the least gate surgery.

## 4. Seam inventory (production adapter + fake count in tests/)

| Seam | Real | Fakes (examples) |
|---|---|---|
| `ITcpRedirectListenerFactory` | `TcpRedirectListenerFactory` | 5+ (Fake/Barrier/Single/Parking/Gated, TcpCoordinatorFakes.cs:154–293) |
| `ITcpRedirectListener` | `TcpRedirectListener` | 3 (Fake/Throwing/ParkingDispose) |
| `ITcpAcceptedConnection` | `TcpAcceptedConnection` | 1 (FakeAcceptedConnection) ⚠ see Issue #2 |
| `ITcpProxyRelayFactory` | `TcpProxyRelayFactory` | 4 (Fake/Gated/Completable/Inline) |
| `ITcpRelay` | `TcpProxyRelay` | 4 (Fake/Completable/EndKind/Faultable) |
| `ITcpRedirectInjector` | `TcpRedirectInjector` | 2 (Fake/Ordering) |
| `IUdpProxyTransportFactory` | `Socks5UdpTransportFactory` | 8 (Gated/Delayed/Failing/CancellationAware/CollidingAlias/Fake/ImmediateFault/StagedGate) |
| `IUdpProxyTransport` | `Socks5UdpTransport` | 2 (Fake/ImmediateFault) |
| `IUdpResponseSink` | `UdpResponseReinjector` | 2 (Fake/Noop) |
| `IPacketReinjector` | NdisApi impl | 4 (Fake/Counting/Recording/ThrowingBatch) |
| `IUdpAdapterTargetSource` | `UdpAdapterTargetSource` | 0 dedicated fakes (real snapshot type used directly — minor gap) |
| Socket-level | — | `TrackingSocket : Socket` (Socks5ProtocolTests:160, Socks5UdpAssociateTests:149) |
| `Socks5ControlConnection` ctor fns | — | `onSocketReady`/`resolveAddresses`/`socketFactory` (Socks5ControlConnection.cs:47–51) |
| `Socks5UdpTransport.CreateAsync` fns | — | `createControl`/`socketFactory`/`disableUdpConnectionReset` (Socks5UdpTransport.cs:158–161; asserted Socks5UdpConnresetTests:46) |

Verdict: seams are **real** — every interface except `IUdpAdapterTargetSource` has ≥2 test adapters, and the barrier/TCS-gated fakes (`BarrierListenerFactory`, `StagedGateTransportFactory`) implement the spec's deterministic-concurrency requirement (quality-guidelines.md:20). `TcpProxyRelay` is directly constructible (`TcpProxyRelayTests:62,87,106` use loopback pairs + faulting streams); the acceptor is tested through `ITcpRelay` fakes including `ITcpRelayEndInfo` capability probing.

## 5. Top 3 strengths

1. **Atomic retire + single tombstone write point** — `TcpRedirectSessionStore.RetireSessionUnderGate` (`TcpRedirectSessionStore.cs:233–246`) performs session removal, `Phase=Closing`, retire, alias removal, and tombstone arming in one critical section, with a documented acyclic lock order (store→table→tombstone, class doc lines 19–25) and `ReferenceEquals`-guarded idempotent removal (`TcpRedirectTable.cs:280,287`). This eliminates the connect-then-instant-death race class entirely.
2. **Mirrored exactly-once budget discipline** — UDP's global setup budget with five enumerated credit sinks (`UdpProxyCoordinator.cs:224–226` comment; flush 475, eviction 241/249, slot drain 580–584, dispose drain 332–338) and TCP's pending-SYN byte budget with four sinks (`TcpPendingSynSetup.cs:28–30`) are the same pattern applied to both protocols, both fake-time testable, both with Interlocked overshoot tolerance documented.
3. **Fail-closed directionality with observable exits** — every TCP injection failure funnels through `HandleInjectionFailureAsync` (`ClientResetInjector.cs:151–166`: warn with nativeError/adapterHandle → best-effort RST → tombstone); the UDP reinjector's direction matrix drops fail-closed with per-reason rate-limited warnings (`UdpResponseReinjector.cs:145–189`). Combined with the X1 prefilter whose miss provably degrades to the slow path (`TcpProxyCoordinator.cs:356–368`), the system's failure modes are enumerable.

## Top issues (ranked)

1. **`UdpProxyCoordinator.cs` = 471 effective lines, spec limit 400** (`directory-structure.md:57`; measured). Six fused responsibilities (admission, budget accounting, dial pipeline, cooldown table, slot lifecycle, logging). Why it matters: it is the only spec violation in the reviewed area, and every cross-cutting fix (e.g. the 2026-09-06 TTL re-stamp, lines 431–442) lands in an ever-growing file — the same dynamic that produced TCP's pre-split 1158-line monolith. Fix: §3 split A–D.
2. **`ITcpAcceptedConnection` is not substitutable at the real relay** — `TcpProxyRelayFactory.EstablishAsync` throws unless the connection is the concrete `TcpAcceptedConnection` (`TcpProxyRelay.cs:28–31`). Why it matters: the acceptor↔factory seam only supports fake relays; any test of the real factory needs real sockets, and a future second listener implementation (e.g. a test double for the drain path) silently breaks at runtime, not compile time. The `internal Socket Socket` accessor (`TcpRedirectListener.cs:73`) makes the downcast unnecessary if the socket were exposed via an internal capability interface instead of a hard type check.
3. **Naming drift: "tombstone" means two opposite things across mirrored coordinators** — TCP `Tombstones` = 60 s post-teardown grace (`TcpRedirectSessionStore.cs:42–46`); UDP `_setupTombstones` = 1 s pre-setup-failure cooldown (`UdpProxyCoordinator.cs:33,144–153`). Why it matters: the specs and code cross-reference both; a reader porting TCP's grace-drop reasoning (never reuse `Blocked`, `tcp-local-redirect.md:143) onto UDP's cooldown, or vice versa, gets subtly wrong expiry/backoff semantics. Renaming UDP's to `UdpSetupCooldownTable` (split A) resolves it mechanically.
4. *(runner-up)* **UdpAdapterTargetSource lacks a test fake** — only the real mutable snapshot type is exercised; the `Host`/`Resolve` contract (null-on-missing) is tested through the concrete class rather than through the interface, so the seam exists but has one adapter.
