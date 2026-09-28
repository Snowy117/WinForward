# Design: UDP association reuse (control-connection pooling)

> Task `09-28-udp-association-reuse`. Status: planning. Decisions D2/D3 in `prd.md` are proposals
> pending the planning approval; D1 (default on + sticky fallback) is settled.

---

## 1. Scope / Trigger

Trigger: any change to `Socks5UdpTransport`, the SOCKS5 UDP control-connection lifecycle, the UDP
session/capacity/retention policy, `UdpProxyCoordinator` teardown reasons, or the UDP side of
`DurableCaptureBundle` composition.

Out of scope (see PRD): relay-socket (alias) sharing, TCP relay reuse, mux transport.

## 2. Shape

Today the transport owns a 1:1 `Socks5ControlConnection` + relay socket pair, and the session owns
the transport. The design inverts that:

```
Socks5UdpTransportFactory(server-keyed)
  └─ UdpAssociationPool                      one per Socks5Server; owned by DurableCaptureBundle
       ├─ QuiescenceScope                     pool lifetime token
       ├─ UdpAssociation[]                    each: own QuiescenceScope + D11 one-shot teardown
       │    ├─ Socks5ControlConnection         authenticated once; ASSOCIATE once
       │    ├─ RelayEndpoint + family          mutable on in-place re-association
       │    ├─ lease records                   attached transports (weak-ish, refcounted)
       │    └─ watchdog                        scope.Run child: post-ASSOCIATE read + 5 s sampling tick
       └─ ServerCapability                     Unknown | SharedOk | PerFlowOnly (sticky per run)
UdpAssociationLease : IAsyncDisposable        borrowed by exactly one transport
Socks5UdpTransport                            holds (lease, socket, relay Endpoint, self-traffic token)
```

Ownership rules (per `async-lifetime.md`):

- The pool and each association are owners: one `QuiescenceScope` each (D7), explicit D11 one-shot
  teardown claims, and every watchdog/recovery/re-association child is a `scope.Run` child (WF0003).
- Nested lifetime: the bundle's disposal order becomes sweeper → UDP coordinator → pools/setup
  executor → **association pool**; the pool's drain seals, then joins every association's drain,
  which joins every watchdog child. No session may outlive a lease (I1).
- The transport stops owning the control connection: its `DisposeAsync` returns the lease
  (refcount--) and keeps disposing only the socket, self-traffic token and send gate. This keeps
  `UdpProxySession.DisposeCoreAsync` untouched.

## 3. Contracts

- **I1 — Lease exactly-once**: a lease is released exactly once, by the transport that borrowed it,
  including every construction-failure path (socket bind failure, self-traffic registration failure).
  `UdpAssociation.LeaseCount` reaching zero parks the association in the warm pool.
- **I2 — Reverse routing unchanged**: every flow keeps its own local relay socket, so `RelayAlias`
  stays unique per flow (`UdpSessionSetup.cs:99-112` untouched) and `UdpAssociationTable` semantics do
  not change in this phase.
- **I3 — Hot path unchanged**: no pool interaction per datagram. The transport keeps its warm
  synchronous send shape; the only additions are two `Interlocked` counters (`DatagramsSent`,
  `SawResponse` flag) and a field read of the current relay `SocketAddress`. Allocation gates stay 0 B.
- **I4 — Association fault propagation (piggyback, revised 2026-09-28 during Step 2 planning)**: an
  unrecoverable association marks its lease faulted; each attached transport fails fast on its next
  send with a typed `UdpAssociationLostException` instead of writing to a dead relay endpoint. The
  coordinator's existing send-failure path removes the slot with
  `UdpTeardownReason.AssociationLost` (counted, no cooldown), so recovery happens on the flow's next
  datagram. This replaces the earlier "synchronous fault callback on `IUdpProxyTransport`" shape:
  the callback would have added a member to a public interface (churning every fake and benchmark
  transport) to serve a cold path, and its registration race (the association can die between
  transport construction and session attachment) would have needed its own handshake. The cost of
  the piggyback is bounded: a receive-only flow whose association died keeps its session until the
  30 s idle expiry (Step 1 retention) instead of being torn down eagerly, and one datagram per
  affected flow is dropped fail-closed. Parked receive loops are still ended by session teardown.
  No `_ =`, no `Task.Run`, no new fire-and-forget (WF rules).
- **I5 — No cooldown on association loss**: `RemoveSlotAsync` arms the 1 s setup cooldown only for
  `SetupFailure`, so `AssociationLost` recovers on the next datagram. Pinned by test.
- **I6 — Bounded blast radius**: `MaxAssociationsPerServer = 16` (internal constant) and
  `FlowsPerAssociation = 16`; a new lease over the cap opens a new association, and flows are hashed
  by local port so a death affects at most one fan-out slice. Exceeding the association cap falls back
  to per-flow associations for that server (never refuses the flow).
- **I7 — In-place re-association**: on watchdog-detected death the association re-dials and
  re-ASSOCIATEs; if the new relay endpoint's address family matches the old one, it publishes the new
  `SocketAddress` to attached transports (`Volatile.Write`) and no session is lost. A family change or
  a failed recovery is an association fault (I4).
- **I8 — Sticky capability**: `PerFlowOnly` is per server and lasts the run; existing shared
  associations drain naturally (no new leases), existing sessions are never torn down by the flip.

## 4. Data flow

- **Setup (new flow)**: `UdpProxyCoordinator.TrySendSpanAsync` → `UdpSessionSetup.CreateSessionAsync`
  (unchanged) → `Socks5UdpTransportFactory.CreateAsync(server, ct)` → `pool.RentAsync(ct)`
  (`SharedOk`/`Unknown`: warm association or new ASSOCIATE; `PerFlowOnly`: a private association) →
  bind relay socket in the relay's family → register self-traffic → construct transport with the lease.
  The 8-wide `_setupLimiter` stays; its wait collapses to microseconds when the pool is warm (only a
  cold dial pays the network).
- **Send (established)**: unchanged path, except the destination `SocketAddress` is read from the
  transport's mutable field (updated only by I7).
- **Response (established)**: unchanged; the transport also sets its `SawResponse` flag on the first
  successful relay receive (one `Interlocked.Exchange`, no allocation).
- **Association death**: watchdog faults → recover (I7) or fault (I4) → sessions removed with
  `AssociationLost` → next datagram re-sets up through the pool.

## 5. Capability detection (R2)

Passive, sampled by the association's watchdog tick (every 5 s), no datagram-path work:

| Condition | Verdict |
|---|---|
| `attached >= 2` and at least one attached transport sent ≥ 3 datagrams with no response, while a sibling has ≥ 1 response | `PerFlowOnly` (warn `udp.association.fallback`, counter) |
| `attached == 1` | `Unknown` (keep sharing — no evidence either way) |
| any response on a second attached transport | `SharedOk` (sticky positive; detection stops) |
| `udpAssociationReuse: always` | detection disabled (`SharedOk` forced) |
| `udpAssociationReuse: off` | `PerFlowOnly` from the start (rollback mode) |

The rule is deliberately conservative in the loss direction: a false `PerFlowOnly` costs performance
only (today's behaviour), while a false `SharedOk` would cost datagrams.

## 6. Budget and observability (R4/R5)

Configuration (all validated in `ConfigurationModels.cs` with errors/warnings in the
`tcpFlowCapacity` style):

| Key | Default | Range | Warning |
|---|---|---|---|
| `udpAssociationReuse` | `auto` | `auto \| always \| off` | — |
| `udpSessionCapacity` | 16384 | 1..16384 | > 4096: ephemeral-port headroom (2 ports/flow) |
| `udpRelayReceiveBufferKb` | 128 | 16..1024 | > 256 with capacity > 2048: kernel buffer total |
| `udpSessionIdleSeconds` | 30 | 5..600 | — (sweeper interval becomes `max(5 s, idle/2)`) |

Counters (constants on `RuntimeCounters`) and heartbeat fields: `udpCapacityRejections`,
`udpSetupFailures`, `udpAssociationLost`, `udpAssociationRecovered`, `udpAssociationFallbacks`; the
heartbeat adds `udpAssociations`, `udpLeasedFlows`, `udpRelaySockets`, `udpRelayBufferMB`. The
capacity/setup rejection paths gain a rate-limited `warn` (today: trace only).

## 7. Compatibility, migration, rollback

- Behaviour change: default-on sharing means the SOCKS5 server sees one authenticated session per
  association instead of one per flow. Mitigations: sticky fallback (R2), bounded fan-out (I6),
  watchdog + in-place recovery (I7), `off` mode (R7).
- Rollback: `udpAssociationReuse: off` restores today's per-flow behaviour with the same code paths
  (the pool then behaves as a per-flow allocator), so no revert commit is required for an operator.
- Spec updates at Phase 3.3: `udp-relay.md` (pooling contract, retention/buffer policy, new teardown
  reason), `hot-path.md` (unchanged zero-allocation claim + the two counters), `error-handling.md`
  (association-loss fail-closed + no-cooldown rule), README config table.

## 8. Trade-offs and rejected alternatives

- **Rejected: per-server opt-in** (user decision D1) — slower payoff; replaced by default-on +
  fallback.
- **Rejected in this phase: relay-socket sharing** — needs per-destination claiming and adds
  same-socket head-of-line blocking; tracked as the follow-up.
- **Rejected: mux transport** — requires a cooperating server; already a separate task.
- **Rejected: keep 512 KiB per socket** — with per-flow sockets retained for same-remote churn, the
  kernel-memory term would still grow with the concurrent flow count; the buffer becomes a validated
  knob with a documented budget instead, and the retention tail shrinks from ~2.5 min to ~30 s.
- **Rejected: `Task.WhenAny`-style receive racing** for association loss — allocates and complicates
  the warm path; the synchronous fault callback mirrors the existing receive-failure signal.

## 9. Change boundary

New: `UdpProxy/UdpAssociationPool.cs`, `UdpProxy/UdpAssociation.cs`, `UdpProxy/UdpAssociationLease.cs`
(each ≤ 400 effective lines; split further if a file crosses the cap).

Modified: `Socks5/Socks5UdpTransport.cs` (ownership inversion, mutable relay address, counters,
`UdpAssociationLostException` fail-fast, buffer from options), `Socks5/Socks5UdpTransportFactory`
(pool-keyed), `Socks5/Socks5ControlConnection.cs` (watchdog read seam + optional keepalive),
`UdpProxy/UdpTeardownReason.cs` (+ `AssociationLost`) and the coordinator's send-failure mapping,
`UdpProxy/UdpProxyOptions.cs`, `Configuration/ConfigurationModels.cs` (+ `udpAssociationReuse`),
`Cli/UdpProxyComposer.cs`, `Cli/DurableCaptureBundle.cs` (pool creation/disposal order),
`Runtime/IdleExpirySweeper.cs` (interval), `Runtime/RuntimeCounters.cs` + `RuntimeHeartbeat.cs` +
`Cli/Program.cs` (fields), `benchmarks/.../Stability/*` (new `udp.sessionBudget` scenario), tests
below. `UdpProxySession` is deliberately untouched (I4's piggyback shape needs no per-session
callback).

Explicitly not touched: `UdpProxyCoordinator` slot/gate logic, `UdpSessionSetup` pipeline,
`UdpAssociationTable`/`RelayAlias`, `UdpResponseReinjector`, NDISAPI layer.

## 10. Tests required

- `UdpAssociationPoolTests`: rent/return refcount, warm reuse, fan-out hash, cap overflow mode,
  drain joins lease holders, single-flight teardown, no orphan sockets.
- `UdpAssociationCapabilityTests`: pinning-detection rule (positive, negative, single-attach
  `Unknown`), sticky verdict, `always`/`off` overrides, flip keeps existing sessions alive.
- `UdpAssociationRecoveryTests`: watchdog detects a dropped control connection, in-place
  re-association keeps sessions and their sockets, family change faults them with
  `AssociationLost`, no setup cooldown armed, no unobserved task exception.
- `Socks5UdpTransportLeaseTests`: lease released exactly once on every construction-failure path;
  `off` mode reproduces per-flow associations byte-for-byte.
- `UdpRelayTests` / `UdpProxyCoordinatorTests` / `UdpSessionSetupTests`: green unchanged, plus a new
  case asserting N flows share K associations through the production factory.
- Fake SOCKS5 servers (TestHelpers): permissive multi-source-port, source-port-pinning, and
  drop-the-control-connection servers.
- Soak: `udp.sessionBudget` (flat sockets/ports/fds/buffer estimates at ≥100 new flows/s, ≥1 h
  nightly), plus re-runs of `udp.churn`, `udp.burstEstablishment`, and `udp.lossRate` matrices.
- Gates: `HotPathAllocationGateTests.EstablishedUdpDatagramPathAllocatesNoManagedBytes` = 0 B;
  `dotnet format … --verify-no-changes` empty; `dotnet build -c Release` 0 warnings;
  `dotnet test -c Release` green; `jb inspectcode` 0 issues.
