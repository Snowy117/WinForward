# Research: Cross-Report Synthesis & Ranked Backlog (2026-08-30)

- **Purpose**: deduplicate and rank the findings from the four path reviews (this directory) into one actionable backlog; record the main-agent spot-check verification evidence so future tasks can trust the top claims without re-verifying.
- **Baseline**: commit `e5667af` (master, clean), 431/431 tests, zero-warning build.
- **Positioning note (user-stated)**: the product's core value is SOCKS5 proxy forwarding; pass/block are byproducts — the backlog below is ordered accordingly.

## 1) Spot-Check Verification Evidence (2026-08-30)

Three highest-impact claims were re-verified by direct source read before being promoted to the backlog:

1. **Production warm-path dead entry — CONFIRMED.**
   `FlowDispatcher.cs:129`: `if (_logger.IsEnabled(RuntimeLogLevel.Trace) || _reverseHandler is not null) return DispatchSlowAsync(packet, cancellationToken);`
   `Program.cs:271` (run composition): `reverseHandler: tcpCoordinator.HandleReverseIfApplicableAsync,` — wired unconditionally, so the non-async warm shape (`FlowDispatcher.cs:131-153`) never executes in production; the 160B/288ns dispatch numbers were measured in compositions without a reverse handler.
2. **Mid-flow relay end has no client-visible reset — CONFIRMED.**
   `TcpRedirectAcceptor.cs:150-168`: `ObserveRelayCompletionAsync` awaits `relay.Completion`, swallows all exceptions (`catch { }`), then calls `_tearDownSession`. No `TryInjectClientResetAsync` on this path; grep shows all call sites are setup-failure helpers inside `ClientResetInjector.cs` itself (`:127,139,164`).
3. **No `NoDelay` anywhere — CONFIRMED.**
   `rg "NoDelay|KeepAlive|ReceiveBufferSize|SendBufferSize" src/` returns only `GC.KeepAlive` hits and the UDP `RelaySocketReceiveBufferSize` (512 KiB, `Socks5UdpTransport.cs:89,149`). No TCP socket option is ever set.

## 2) Structural Performance Findings (cross-report, deduplicated)

| # | Finding | Evidence | Why it matters |
|---|---------|----------|----------------|
| X1 | **Hot dispatch entry dead in production** (reverse-handler diversion is unconditional) | `FlowDispatcher.cs:129` + `Program.cs:271` | Every landed warm-path optimization (160B/288ns shape) is inert in real runs; all packets pay the async slow-path cost |
| X2 | **Two full frame copies per relayed TCP packet; one per UDP datagram** (deferred-materialization item) | `TcpProxyCoordinator.cs:152,204,297` → `PacketRuntime.cs:74-83`; `TcpRedirectInjector.cs:12-24`; `NdisPacketActionExecutor.cs:134` | The known "669B/pkt phase-2 clones"; removable under the already-proven batch-slot stability contract (in-place Pass reinjection uses it) |
| X3 | **Single-IOCTL reinjection despite imported batched sends** | `NdisApiDriver.cs:185-209`; unused `NdisApiAbi.cs:217-227` (`SendPacketsToAdapter/Mstcp`); reusable `BuildMultiRequest` `NdisApiDriver.cs:138-183` | 32 same-direction packets per pump batch = 32 kernel crossings → 1 |
| X4 | **No `NoDelay` on either TCP relay leg** | `TcpRedirectListener.cs:42-48`; `Socks5ControlConnection.cs:110-127` | Nagle × delayed-ACK = 40–200ms latency cliffs on interactive traffic through a byte-pipe proxy |
| X5 | **8 KiB fixed pump buffers** | `TcpProxyRelay.cs:62,167` | Syscall/memcpy per-byte cost ~8× higher than a 64 KiB pooled buffer |
| X6 | **UDP endpoint round-trip allocations** (3–5 per datagram pair) | `UdpProxySession.cs:115`; `Socks5Udp.cs:108`; `Socks5UdpTransport.cs:210,271`; `IPAddressValue.cs:97-105` | Only significant warm-path allocation source left in UDP steady state |
| X7 | **Per-packet lock traffic**: unconditional reverse double-lookup, SelfTraffic global lock + 4 probes, FlowTable lock+Touch write, adapter-gate-map lock per native call, sweep LINQ under locks (+ dead `TcpRedirectTable.RemoveExpired`) | `TcpProxyCoordinator.cs:299→201`; `SelfTrafficRegistry.cs:25-34`; `Domain.cs:183-189`; `NdisNativeCallGate.cs:70-82`; `TcpRedirectSessionStore.cs:119-122`; `TcpRedirectTable.cs:277`; `UdpProxyCoordinator.cs:368-371` | Convoys at high aggregate pps / multi-adapter hosts; periodic sweep hiccups |
| X8 | **Stall-window re-arm per chunk; no ServerGC** | `TcpProxyRelay.cs:174,191`; `WinForward.Cli.csproj` | 5–10% of a core at 10Gbps single flow; GC pauses during session storms |
| X9 | **SOCKS5 worst-case setup budget ~150s** (30s DNS + 30s×4 attempts — earlier notes said 10s×2, code says otherwise) | `Socks5ControlConnection.cs:14-15,59-60,62` | Black-holed upstream holds a session slot and delays the first datagram ~150s before failing closed |

## 3) Structural Stability Findings (cross-report, deduplicated)

| # | Finding | Evidence | Failure scenario (short) | Severity |
|---|---------|----------|--------------------------|----------|
| R1 | **Mid-flow relay fault/stall → client blackhole** (no client RST; stall path returns normally, skipping even the swallowing catch) | `TcpRedirectAcceptor.cs:150-168`; `TcpProxyRelay.cs:100-106,117-122`; tombstone eats the teardown RST at `TcpProxyCoordinator.cs:270-278,324` | Upstream RSTs → client retransmits into tombstone for minutes → ETIMEDOUT instead of instant abort. Fix = one call site + a `RelayEndKind` on the relay; `ClientResetInjector` already builds the in-window RST\|ACK | HIGH |
| R2 | **Two-lock retire/removal window** (flagged residual `TcpRedirectSessionStore.cs:293-294`) | `TearDownSessionAsync:201-209` → `RemoveAssociationFromTable:293-296`; reuse check `coordinator:112` / `TryClaim` `TcpRedirectTable.cs:164-169` ignore `Phase==Closing` | Same-tuple SYN in the µs–ms gap lands on the about-to-be-disposed listener → RTO. Fix = move `TryRemove`+tombstone into `RetireSessionUnderGate` (lock order store→table→tombstone verified acyclic) | MED |
| R3 | **Unbounded queue/dictionary growth**: TCP tombstone insertion-order queue (refresh never dequeues); UDP setup-tombstone dict between sweeps | `TcpRedirectTombstoneTable.cs:48-62,78-98`; `UdpProxyCoordinator.cs:23,433` | Days of uptime × churn → hundreds of MB (TCP); port-scan cycles (UDP) | MED |
| R4 | **UDP setup aggregate memory has no global byte budget; buffered datagrams have no TTL** | `UdpProxyCoordinator.cs:13-15,129` (32KB × up to 16384 flows, 8-way serialized setup, 30s per attempt drain) | Flash crowd + slow/dead server → worst case ~512MB held for hours, then delivers long-expired packets | MED-HIGH |
| R5 | **Jumbo mismatch → teardown/handshake storm + oversized-response blackhole** | `Socks5UdpTransport.cs:267` (1536B hard-coded) vs `maximumFrameSize` (`UdpProxyCoordinator.cs:48`, `UdpResponseReinjector.cs:69`); oversize drop `:284,301`; catch-all teardown `UdpProxyCoordinator.cs:166` | Each >1508B datagram on jumbo deployments re-runs the full SOCKS5 handshake; near-1500B server responses (QUIC/WireGuard) drop silently | MED |
| R6 | **No TCP keepalive + 30-min stall kill + slot squatting** | `TcpProxyRelay.cs:69` | Idle-but-healthy flows silently killed (via R1 blackhole); 1-byte-per-29-min squatters exhaust capacity | MED |
| R7 | **Driver transient error → global fail-closed, no retry** | `NdisApiDriver.cs:124-133`; exit path `Program.cs:223-227` | Adapter removal / power transition / driver pause kills interception for all adapters and exits | MED |
| R8 | **TCP SYN inline listener allocation blocks the adapter pump** | `TcpRedirectSetup.cs:59-75` (alloc+bind on the pump thread; UDP setup correctly offloaded) | Port-exhaustion / slow-bind stalls the entire adapter batch (head-of-line) | MED |
| R9 | **Fire-and-forget tails can throw unobserved** | `TcpRedirectAcceptor.cs:90,167,96,101`; `UdpProxySession.cs:224` | Any teardown/accept-path throw → unobserved task exception → potential process crash | LOW |
| R10 | **Pump DisposeAsync/RunAsync ordering only conventionally safe; per-flow kernel memory ~300KiB → ~4.5GiB at capacity** | `NdisCapture.cs:93-98` vs `CaptureLifecycle.cs:107-111`; buffer defaults | Type-level use-after-free permitted (protected by call ordering today); capacity gate bounds flows, not bytes | LOW-MED |
| R11 | **UDP reverse index + `RemoveExpired` are production dead code** (false safety net) | `UdpAssociations.cs:92,101,141` (test-only callers; real routing via `FlowDispatcher.IsReverseOf`) | Maintenance hazard / false assurance | LOW |

Verified-sound areas (no action): OCE token discipline; self-traffic registration TOCTOU closed (Bind→register→SYN ordering, generation-guarded remove); pool exactly-once CAS returns; in-place mutation read-then-write invariant; SOCKS5 reply parser discrimination (L2) and per-stage timeouts; ABI size/offset asserts; relay dispose-vs-fault-observer ordering; UDP ready-flip/queue-emptiness critical section; `TryBeginExpiry` activeSends==0 protocol.

## 4) Measurement Gaps (risk order)

1. Windows real-NDIS data plane zero benchmarks (fake reader in CapturePumpBench; VM loopback ~4.8k pps ceiling caps all Windows soak numbers).
2. `WSAEADDRINUSE` 8.2% at only 64 concurrent churn (recorded in `benchmarks/results/2026-08-29-windows-real-machine/README.md`); `tcpFlowCapacity` 4096 never stressed.
3. No per-connection added-latency / p99-p999 distributions; fake server always succeeds (no auth failure, CONNECT refusal, server-side degradation, retry/backoff numbers).
4. No hours-scale soaks (tombstone/queue growth R3/R4-class issues invisible to 60s runs).
5. IPv6 data plane: functional only, no perf numbers.
6. Remaining: DNS-shaped load scenario, MTU/fragment trigger rates, reverse-leg reinjection latency, GC pause measurement, auth-variant handshakes. Details in `measurement-gaps.md` §C.

## 5) Ranked Backlog (merged, effort vs payoff)

| Pri | Action | Source items | Effort | Note |
|-----|--------|--------------|--------|------|
| 1 | **Revive the production hot dispatch path**: (a) gate reverse diversion to TCP-only packets; (b) full fix = listener-port bitmap prefilter (65536-bit, atomically swapped) so non-candidate TCP skips the slow path | X1 | S (a) / M (b) | Pure win: re-activates all landed warm-path optimizations; (a) is hours |
| 2 | **Client-visible RST on relay fault/stall end** + `RelayEndKind` surface | R1 | S (~0.5 d) | Every upstream fault currently = minutes-long blackhole; injector infra is built, one call site missing |
| 3 | **`NoDelay` on both legs + 64 KiB pooled pump buffers + stall re-arm throttle** | X4, X5, X8 | S (~0.5 d total) | Latency cliffs + per-flow throughput ceiling + timer-op overhead in one small package |
| 4 | **Atomic retire+remove+tombstone + bound the tombstone/setup queues** | R2, R3, R4 | S-M (~1 d) | Closes the flagged residual; adds the missing aggregate memory bounds + flush TTL |
| 5 | **UDP allocation zero-out + buffer sizing consistency (fixes jumbo storm + oversize blackhole)** | X6, R5 | S (~1 d; jumbo end-to-end M) | Warm path to near-zero alloc; eliminates the handshake-storm failure mode |
| 6 | **Zero-copy proxy data path** (in-place rewrite+`RetagForReinjection` on native buffer; UDP span parse) | X2 | M | The "phase-2 669B/pkt" item; lands inside the existing batch-slot contract |
| 7 | **Batched reinjection IOCTLs** (multi-request flush per same-direction pump batch) | X3 | M | 32→1 kernel crossings for pass/reinject loads |
| 8 | **Keepalive + configurable/lower stall default + lock cleanup (merged reverse lookup, SelfTraffic COW, sweep de-LINQ, dead-code removal) + ServerGC** | R6, X7, X8, R11 | S cumulative | Hardening bundle; several one-liners |
| 9 | **Windows real-machine benchmark + hours-scale soak program** (NDIS reinjection cost, WSAEADDRINUSE vs port budget at 64→4096 churn, added-latency percentiles) | Gaps 1–4 | M | Determines where the next optimization dollar goes; evidence program, not code |
| 10 | **Transient driver-error retry/backoff + offload TCP listener allocation from the pump** | R7, R8 | M | Long-running-host resilience |

Deferred/optional: SOCKS5 handshake allocation diet (70.6KB/conn baseline exists), tighter total setup budget than 150s (X9), TCP socket buffer sizing/capacity documentation (R10), per-flow DNS cache, `DispatchNonFlowAsync` non-async variant, IPv6 perf benchmarks (fold into #9).

## 6) Suggested Task Tree Shape (when picking the work up)

- **fast-hardening** (items 2+3, maybe 4): one child task, S effort, immediate user-visible wins (latency, fault visibility, memory bounds).
- **hot-path-revival** (item 1): one child; (a) is a prerequisite quick win even if (b) lands later.
- **zero-copy-datapath** (item 6) and **batched-ioctls** (item 7): separate children, both touch `NdisApiDriver`/executor seams.
- **udp-alloc-and-jumbo** (item 5): one child.
- **windows-reality-program** (item 9): a measurement task (no product code), pairs with the archived follow-ups already recorded in `08-29-proxy-stability-perf/prd.md` and `2026-08-29-windows-real-machine/README.md`.
