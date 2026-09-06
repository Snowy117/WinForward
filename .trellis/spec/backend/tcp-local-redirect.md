# TCP Local Redirect Contracts

> How WinForward transparently redirects host and forwarded TCP flows to a local SOCKS5 relay: the WinpkFilter local_redirect transform, forwarded DNAT shape, client-reset lifecycle, and teardown grace. Split 2026-08-29 from the former monolithic NDISAPI file; NDISAPI transport basics live in [windows-ndisapi.md](./windows-ndisapi.md).

---

## WinpkFilter local_redirect transform (hardware-verified 2026-08-08, Win11)

The official WinpkFilter transparent-TCP-redirect pattern (`ndisapi::local_redirector`, used by socksify/ProxiFyre) is NOT "rewrite dst to loopback + SendToMstcp". It is:

1. Swap Ethernet src/dst MACs.
2. Swap IP src/dst.
3. Rewrite `th_dport` to the local proxy port. **The client's source port (`th_sport`) MUST be preserved** — the redirector only rewrites th_dport. The local proxy server's accepted connection then has peer = server_ip:client_orig_port, which the per-flow mapping resolves by client source port.
4. Recompute IP + TCP checksums (the pseudo-header uses the swapped addresses).
5. The listener binds `0.0.0.0:proxy_port` (all interfaces), not loopback — the rewritten packet's destination is the client's own IP address + proxy port.

Attempting dst=loopback + SendToMstcp produced a byte-correct frame (verified checksums) that MSTCP silently ignored — the local-redirect contract requires the IP-swap form.

### Reverse path (the subtle part)

The SYN-ACK that MSTCP emits in response to an injected (`SendPacketsToMstcp`) SYN is a reverse packet (source port = proxy port). It must be recognized and reversed BEFORE flow-table lookup and policy evaluation:

- A dispatcher-level reverse hook (`TcpProxyCoordinator` implementing `ITcpReverseHandler`, wired as `FlowDispatcher._reverseHandler`, dispatched from `FlowDispatcher.TryHandleReverseAsync`) runs right after the self-traffic check. If the packet's local/remote endpoint pair matches a `ReverseRedirectTuple` (full pre-rewrite wire tuple, never a listener port alone), it is reversed (`src -> original server:port, dst -> original client:port`, MACs swapped) and injected toward MSTCP. This prevents the reverse packet from being re-evaluated as a new client flow (which policy would silently `pass`, killing the handshake).
- **Warm-entry diversion precheck (X1, 2026-08-30)**: `ITcpReverseHandler.WantsPacket(in CapturedFlowPacket)` is consulted ONLY on the dispatcher's non-async warm entry — divert to the slow path iff `protocol == Tcp && src port ∈ active listener-port set` (`TcpRedirectTable.IsReverseCandidatePort`, reference-count array). The count increments inside `TryClaim` under the table gate BEFORE the rewritten SYN is injected (the SYN-ACK can never precede port visibility) and decrements in `TryRemove` (ReferenceEquals-guarded) / `RemoveExpired`. The slow path always runs the full handler regardless of `WantsPacket` — prefilter misses (e.g. tombstone-window stragglers with the port already decremented) fall through to the slow path because listener-shaped tuples never resolve in any `FlowTable.TryResolve` mode, so behavior degrades to the pre-X1 slow path, never to wrong routing.
- Without the hook, the reverse packet is evaluated by policy (process attribution can't match the injected tuple) and passed straight to the wire — the client never receives its SYN-ACK and the connection times out. This was the dominant failure mode during bring-up.
- **LoopbackFilter (0x20) is NOT required** for this reverse path: the hook catches the reverse packet on the normal capture path. (Loopback filtering was investigated; enabling it caused the injected SYN's loopback reflection to be re-captured, which then had to be drained.)

### Mid-flow data

After the handshake, client -> listener data on the original flow must also be rewritten to the proxy tuple and reinjected (`ReinjectExistingFlowDataAsync`: same swap, dst -> proxy port). A flow with an active redirect association is recognized by `TryResolveByOriginal` (`TcpRedirectTable`); anything else is not ours.

### Data-bearing SYNs (TCP Fast Open) are tolerated, never blocked (2026-09-06)

A client SYN carrying data (TFO, RFC 7413) rides the exact same redirect pipeline as a bare SYN: `TcpFrameRewriter.IsTcpSyn` is a boolean SYN predicate (SYN set, ACK clear — no payload discrimination), and `TcpProxyCoordinator.HandlePacketAsync` routes every SYN into `HandleSynAsync`. Why tolerance needs no extra support: the forward-leg rewrite is an RFC 1624 incremental update over addresses/ports only (payload bytes are never touched), `TcpSequenceObservation.TryReadTcpSequenceAdvance` already counts SYN data in the sequence advance (`payloadLen + SYN + FIN` from IP totalLength), the RST template is header-only, and the non-TFO local listener stack queues or drops the SYN data, after which the client retransmits it post-handshake (RFC 7413 graceful degradation) — the relay sees a normal stream either way. Blocking data-bearing SYNs (the pre-2026-09-06 `TcpSynKind.WithPayload → Blocked` fast path) only blackholed TFO clients: the executor consumed every retransmission silently and the client died at ETIMEDOUT. Locked by `SynWithPayloadIsRedirectedLikeBareSyn` (payload survival + checksum validity) and `RetransmittedSynWithPayloadReusesAssociation` (`ClientNextSeq == ISN + 1 + payloadLen`) in `TcpProxyCoordinatorRewriteTests`.

### Redundant accepts

A retransmitted SYN can make MSTCP open a second connection on the same listener. After the first relay is established, further accepts must be drained and closed immediately (`DrainRedundantConnectionsAsync`, `TcpRedirectAcceptor.cs`) rather than starting a second relay — otherwise every extra accept fails with SocketException and the log floods.

**Reference**: `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs` (HandleSynAsync/HandleReverseAsync/ReinjectExistingFlowDataAsync/HandleReverseIfApplicableAsync), `TcpRedirectAcceptor.cs` (accept loop + DrainRedundantConnectionsAsync), `TcpRedirectListener.cs` (0.0.0.0 bind), `src/WinForward.Runtime/FlowDispatcher.cs` (`_reverseHandler`).

### Forwards-direction reverse injection (hardware-verified complement)

Reverse-packet injection direction follows the flow origin:

- Host-originated flow (client on this host): the reversed packet goes to MSTCP (`SendToMstcp`).
- Forwarded flow (client behind a VM/remote adapter): the reversed packet must go back to the origin adapter (`SendToAdapter`), not MSTCP.

`TcpRedirectInjector.InjectAsync(frame, towardMstcp, adapterHandle, ct)` selects the direction; the coordinator passes `association.OriginalKey.Origin == FlowOriginKind.Host`. Locked by `ForwardedFlowReverseInjectsTowardOriginAdapter` / `HostFlowReverseInjectsTowardMstcp` in the TCP coordinator tests.

> **Note**: the current Win11 test host has no Hyper-V VM stack (the "Microsoft Hyper-V Network Adapter" interfaces exist but no vSwitch/VM is present), so guest-originated forwarded traffic cannot be exercised end-to-end here. The forwarded code path is unit-locked; a host with a real guest VM is required for the hardware matrix.

### Forwarded flows use the DNAT-to-local transform (fixed 2026-08-14)

The WinpkFilter IP-swap transform above is only valid for **host-originated** flows. Applied to a forwarded SYN it produces dst = client-ip:listener-port, which is not a local address — MSTCP routes the frame back out to the client, the listener never sees a SYN, and the flow hangs (observed on the 192.168.77.x gateway: `tcp.relay.started` stayed 0 while mangled frames were passed back to the client). Forwarded flows therefore use a different shape, selected per association by `TcpRedirectAssociation.ForwardLocalAddress`:

- Forward leg (SYN and mid-flow data, `TryRewriteForwardLeg`): **src stays client:client-port; only dst moves to (adapter-local address L, listener port)**. L is resolved per origin adapter via `IAdapterLocalAddressProvider` (wired as `WindowsAdapterLocalAddressProvider`, `src/WinForward.Windows/AdapterLocalAddressProvider.cs`): IPv4 prefers a same-subnet address, IPv6 skips link-local and prefers a /64 prefix match. **Never 127.0.0.1** — the reverse reply from a loopback destination would carry a martian source and can be dropped by the stack before reaching the capture layer for rewriting. No L candidate → fail-closed `Blocked` (`tcp.redirect.rejected reason=localAddress`).
- No MAC swap on the forward leg: the arrival frame's dst MAC already addresses this host. The MAC swap is now gated on `towardMstcp` everywhere (host shape swaps; forwarded never does).
- Table endpoints follow the shape: forwarded `ReverseSource = (L, listener-port)`, `ReverseDestination = AcceptedPeer = (client, client-port)`, so the accept-loop peer validation and `TryResolveByReverse` see the real client tuple.
- Reverse leg is unchanged textually (`src -> original server:port, dst -> original client:port`) and still injects to the origin adapter; the association's origin-shaped endpoints make the same rewrite call correct for both shapes.
- The wildcard self-traffic registration `(Tcp, 0.0.0.0:P, 0.0.0.0:P)` cannot match the forwarded SYN-ACK `(L:P -> client:port)` because the remote leg must equal the observed remote exactly — the reverse hook still owns that packet.
- Locked by `ForwardedFlowSynRewritesTowardAdapterLocalListener`, `ForwardedFlowWithoutLocalAddressFailsClosed`, `ForwardedFlowAcceptsClientTuplePeer`, and the rewritten `ForwardedFlowReverseInjectsTowardOriginAdapter`.

### Relay setup failure resets the client (fixed 2026-08-14)

When the SOCKS5 relay cannot be established after a successful redirect (proxy down, auth failure, upstream unreachable), the client's connection is already established through the redirect leg and would otherwise hang. The coordinator records the client ISN (+ a bounded copy of the original SYN) at setup and the server ISN when the reverse SYN-ACK passes the hook; on relay failure it crafts a standalone RST|ACK (`TcpResetBuilder`, `src/WinForward.Protocols/TcpResetBuilder.cs`, fresh checksums, MACs mirrored from the SYN template) with seq = server-ISN + 1 — in-window for the client's established state — and injects it toward MSTCP (host) or the origin adapter (forwarded) before tearing the session down. Missing sequence numbers degrade to plain teardown. Locked by `RelaySetupFailureInjectsClientResetWhenSequencesKnown` / `ForwardedRelayFailureInjectsClientResetTowardOriginAdapter` / `RelaySetupFailureBlocksAndReleasesAlias` (degradation) and `TcpResetBuilderTests`.

---

## Client-reset sequence tracking and injection-failure exits (wired 2026-08-28)

- **Tracked sequences beat ISN+1**: `TcpRedirectAssociation` (`TcpRedirectTable.cs`) observes both directions' `seq + payloadLen` (SYN/FIN each count 1, payload length from IP totalLength — never ethernet frame length, padding pollutes it; IPv6 extension headers deducted) at the two pre-rewrite points, wrap-aware advance-only. `ClientResetInjector.TryInjectClientResetAsync` (`TcpRedirect/ClientResetInjector.cs`) must use `ClientNextSeq ?? clientInitialSeq+1` (ack) and `ServerNextSeq ?? serverInitialSeq+1` (seq): ISN+1 is out-of-window once the client has sent data and the stack silently discards the RST (slow-EOF symptom). New observation sites must read the frame BEFORE rewrite (original bytes) and synchronously (Span must not cross an await).
- **Every `SendPacketTo*` failure exits through `HandleInjectionFailureAsync`** (also in `ClientResetInjector.cs`): warn `tcp.redirect.failed reason=injectionFailure` with nativeError/adapterHandle/flow key → best-effort client RST → `FailAssociationAsync` (tombstone single write point, see teardown grace below). Free-text catches around injections are forbidden — a silent `FailAssociationAsync` after adapter-handle staleness is exactly the invisible-teardown defect this rule exists to prevent.
- **SOCKS5 relay setup budget**: the relay call site (`TcpRedirect/TcpProxyRelay.cs`) passes `RelayConnectMaxAttempts = 2` with `RelayConnectAttemptTimeout = 10s` (internal constants). The default 30s is per-attempt (worst ~150s over multi-address DNS); after redirect accept the client is already established, so every extra budget second is a "connected then reset" second.

---

## Capacity RST, fragment consume+RST, and relay-completion observation (wired 2026-08-29)

Task 08-29-proxy-stability-perf (S3/S4/S1).

### Capacity-rejected SYN → RST|ACK (S4)

- `TcpResetBuilder.BuildResetFromSyn(synFrame, serverTuple, clientTuple)` (`src/WinForward.Protocols/TcpResetBuilder.cs`) reads the client ISN from the observed SYN and builds `seq=0, ack=clientISN+1, flags=RST|ACK` (0x14) — in-window for a SYN_SENT client, which aborts immediately with ECONNREFUSED. No association exists on this path; MACs/IPs are mirrored from the SYN template.
- `ClientResetInjector.InjectCapacityRejectedResetAsync` is **claim-then-inject**: `TcpResetCooldownTable` (`TcpRedirect/TcpResetCooldownTable.cs`, per-4-tuple 1 s window, capacity = session budget, FIFO evict-oldest; a refreshed tuple is NOT re-enqueued) claims the window before any build/inject attempt, so even a failed injection consumes the cooldown — strongest anti-amplification. Injection follows the direction matrix (host → MSTCP, forwarded → origin adapter capture handle); it never throws and never changes the `Blocked` result. Debug event `tcp.redirect.capacityReset`; the existing `tcp.redirect.rejected reason=capacity` trace and `tcp.redirect.capacity` summary are unchanged.
- Locked by the capacity coordinator tests: exactly one RST per tuple per window, retransmissions inside the window silent, new RST after window expiry, direction matrix, injection-failure warn leaves the result unchanged.

### Fragments on associated flows (S1)

- `TcpProxyCoordinator.HandleFragmentAsync` (wired as the `FlowDispatcher` fragment handler, consulted in `DispatchNonFlowAsync` after the self-traffic check): `IPFragment.IsFragment`/`TryReadAddressPair` (`src/WinForward.Protocols/IPFragment.cs`, IPv4 mask `0xbfff`; IPv6 extension-chain walk, nextHeader 44) → `TcpRedirectTable.TryResolveByAddressPair` (third index `_byAddressPair`, direction-agnostic normalized IP pair, same-family gated, last-writer-wins for multi-flow pairs, `ReferenceEquals`-guarded removal — all under the single `_gate`) → hit: trace `tcp.redirect.fragment reason=fragment`, `HandleFragmentTeardownAsync` (best-effort RST via tracked sequences; warn + silent teardown when sequences were never observed; unconditional `_failAssociation` → single tombstone write point), outcome `Dropped`; miss: `NotRelevant` keeps the non-flow pass.
- Dispatcher mapping: fragment-handler `Dropped` → `ProxyConsumed` (silent), `Blocked` → policy path. `CapturedFlowPacket.InspectionSpan` reads the frame without forcing a pooled materialization (ARP/ND frequency on the non-flow path). The hot flow path (`DispatchAsync`) is untouched — non-fragment frames pay one ether-type compare.
- Known residual (accepted): post-tombstone fragments fall back to pass (bounded window); address-pair granularity can tear down the newer of two same-IP-pair associations.

### Faulted relay completions must be observed (S3)

- `TcpRelayFaultObserver.Observe(relay, logger)` (`TcpRedirect/TcpRelayFaultObserver.cs`) attaches an `OnlyOnFaulted | ExecuteSynchronously` continuation on every path that discards a relay without awaiting `Completion`: `TcpProxyRelay.DisposeAsync` (before disposal faults the pumps) and the acceptor's attach-failure branch. Debug event `tcp.relay.faulted`.
- **.NET gotcha (load-bearing)**: attaching a `OnlyOnFaulted` continuation does NOT mark a faulted task observed — only **reading `Task.Exception`** (or awaiting) does. The observer must read `task.Exception` BEFORE any `IsEnabled` log gate; the original implementation checked the log level first and silently left exceptions unobserved under the default `info` threshold (tests passed because the recording logger was always-enabled). Do not reorder.

### Mid-flow relay fault/stall must reset the client (R1, wired 2026-08-30)

Task 08-30-fast-hardening (research R1).

- **Contract**: every relay end that is not a clean FIN-propagated end is surfaced to the client as an in-window RST|ACK via `ClientResetInjector.TryInjectClientResetAsync`, injected in `TcpRedirectAcceptor.ObserveRelayCompletionAsync` **before** `_tearDownSession` — while the association still holds the SYN template and `ClientNextSeq`/`ServerNextSeq` trackers. Without this, the teardown tombstone eats every subsequent client retransmission and the client hangs to ETIMEDOUT (minutes) instead of aborting instantly.
- **Surface**: `TcpProxyRelay` implements the internal capability `ITcpRelayEndInfo { RelayEndKind EndKind }` with `RelayEndKind { CleanEnded, Stalled, Faulted }`, valid after `Completion` completes. The enum is internal, so it lives on a separate capability interface rather than the public `ITcpRelay`; a relay not implementing it is treated as `CleanEnded` (no reset). Derivation: pump returns `PumpResult.Stalled` → `Stalled`; pump faults → `Faulted`; both pumps complete cleanly (FINs propagated via `ShutdownSend`) → `CleanEnded`. **`_endKind` initializes to `Faulted`**: if `RunPumpAsync` throws before its first classification write (e.g. `NetworkStream`/CTS construction), `Completion` faults with the field still at its default — a `CleanEnded` default there would silently skip the client reset and reproduce the blackhole (caught in review 2026-08-30).
- **Ordering & containment**: reset → teardown (injector reads live-association state, matching the `HandleRelaySetupFailureAsync` precedent); the inject is wrapped so a reset failure warns but never blocks teardown; the whole completion tail is wrapped so nothing escapes the fire-and-forget task as an unobserved task exception. OCE on an externally-cancelled (retired) session still returns early without a reset.
- Locked by `TcpRelayEndResetTests`: fault→RST and stall→RST with seq/ack asserted from the advanced trackers (not ISN+1), reset-then-teardown ordering; clean end and end-info-less relay → no injection; real-relay `EndKind` derivation on all three terminal paths.

### New-flow SYN setup never blocks the capture pump (R8, wired 2026-08-30)

Task 08-30-driver-resilience R8 — the TCP counterpart of the UDP setup contract.

- **Pump side (`TcpProxyCoordinator.HandleSynAsync`)**: the synchronous fast paths are unchanged and stay instantaneous — existing-association re-inject (`TryResolveByOriginal`), TIME_WAIT tombstone hit, the new setup-failure cooldown hit (1 s, consumed as `Dropped`), and the capacity gate + S4 RST|ACK (the gate counts `_pendingSyn.ActiveCount` alongside live sessions so the RST fast-fail never depends on background registration timing). A genuinely new SYN takes one path only: copy the frame synchronously (`packet.InspectionSpan.ToArray()` — the pump's native batch slot is recycled the moment the handler returns), retain it in the coordinator-owned `TcpPendingSynSetupIndex`, launch the background setup via `Task.Run`, and return `TcpRedirectOutcome.SetupPending` (executor consumes silently, trace `packet.dropped reason=setupPending`).
- **Pending index bounds** (`TcpPendingSynSetup.cs`, leaf lock never nested under store/table gates): 1024-entry cap (distinct original keys — reject+trace `tcp.setup.pending.dropped`, outcome `Blocked`, same posture as the capacity gate), a 1 MiB global Interlocked byte budget charged on retain and credited exactly once at every sink (retransmission overwrite, completing setup's removal, TTL expiry, dispose drain), a 5 s retention TTL enforced by the idle sweep (the still-running task is unaffected — it captured its own frame reference at launch; a replacement generation meets the table claim as an ordinary concurrent loser), and the 1 s per-flow setup-failure cooldown (bounded, evict-oldest) written only on genuine failure, never on shutdown cancellation.
- **Background (`SetupPendingAsync`)**: wrapped in the store's `EnterSetup`/`finally ExitSetup` inflight drain (dispose semantics preserved; a task that starts after disposal unwinds via the `EnterSetup` `ObjectDisposedException` catch without a cooldown). The pipeline is the existing `SetupNewRedirectAsync` fed a `CapturedFlowPacket` built over the retained copy — claim exactly-once and the concurrent-loser release stay as they were; on claim success the rewrite/injection/session-registration/accept-loop tail runs from the retained copy; a loser re-injects against the existing association; genuine failure (null return or exception) warns and arms the cooldown; shutdown cancellation unwinds without one.
- **Store-gate audit**: the pump side no longer holds `EnterSetup` at all; `RetireSessionUnderGate` (D1) drain semantics now cover background setups through the same inflight counter. Lock order: pending lock (leaf) → store gate → table gate → tombstone gate, verified acyclic.
- Locked by `TcpPendingSynSetupTests` (index bounds, exactly-once credit, TTL, cooldown, cap trace, dispatch-does-not-wait-for-bind, sweep expiry while in flight) and the reworked coordinator suites (absorption burst, pre-claim re-inject, dispose drain of a parked setup).

---

## SOCKS5 control-socket timeout lifecycle (fixed 2026-08-15)

- `Socks5ControlConnection.ConnectOnceAsync` (`Socks5/Socks5ControlConnection.cs`) sets `socket.ReceiveTimeout`/`socket.SendTimeout` to the per-attempt timeout (default 30s; the TCP relay call site passes 10s) as the connect/authenticate ceiling. In .NET, async socket reads/writes honor these timeouts, so any socket handed to a long-lived consumer keeps that per-attempt ceiling.
- `GetUpstreamStream()` (the `TcpProxyRelay` handoff point) MUST reset both to `Timeout.Infinite` before returning the stream — otherwise an idle relay connection dies at 30s via `SocketException(TimedOut)`, defeating the relay's own 30-minute stall window (M4). The CONNECT command (`ConnectDestinationAsync`) runs BEFORE `GetUpstreamStream()`, so the per-attempt window still governs setup. The UDP control socket never goes through `GetUpstreamStream()` and has no post-associate operations, so it is intentionally left unchanged.
- Relay-side idle protection is owned by `TcpProxyRelay.PumpAsync`'s per-operation write/read timeout CTS (30 minutes); the socket-level timeouts are only for the bounded setup phase. Locked by `UpstreamStreamClearsPerAttemptSocketTimeouts` (asserts `ReceiveTimeout == -1 && SendTimeout == -1` after handoff; note .NET reads a disabled timeout back as 0 on Linux and -1 on Windows, so assert `<= 0`).

---

## Deployment: Windows Firewall inbound rule is required (hardware-verified 2026-08-15)

Every redirect path terminates at a local listener socket, and the injected SYN is an unsolicited inbound TCP connection from the stack's perspective. On adapters whose network profile applies the default inbound block (typically Public), the Windows Firewall silently drops that SYN before it reaches TCP: `tcp.redirect.created` appears, no SYN-ACK ever leaves, and the flow hangs. Forwarded DNAT injections onto Private/unidentified-profile virtual adapters passed by default, which is why the failure only showed on the WLAN (Public) host path. Symptom trio: `tcp.redirect.created` present, `tcp.relay.started` absent, zero captures for the listener port. Diagnose with `Set-NetFirewallProfile -All -LogBlocked True` + `pfirewall.log` (DROP to the listener port) and `Get-NetTCPConnection -State SynReceived` (empty). Remedy is a deployment rule, not code: `New-NetFirewallRule -Direction Inbound -Action Allow -Program "<path>\WinForward.exe" -Profile Any`.

---

## Redirect teardown grace and flow-hold contracts (wired 2026-08-28)

### 1. Scope / Trigger

- Trigger: any change to TCP redirect teardown, the `NotRelevant → Pass` fallback, flow-table expiry, or the idle sweeper ordering.

### 2. Signatures

- `TcpRedirectTombstoneTable` (`TcpRedirect/TcpRedirectTombstoneTable.cs`) — dual-key (`FlowKey` forward + reverse `Endpoint` pair) → shared entry with `ExpiryUtc`; `TryAdd(forward, reverseSource, reverseDestination, expiryUtc)` (FIFO evict-oldest at capacity), `TryHit(FlowKey, now)` and `TryHit(reverseSource, reverseDestination, now)` (hit only while `now < ExpiryUtc`), `RemoveExpired(now)` (also head-drains the insertion-order queue).
- `TcpRedirectOutcome.Dropped` (`TcpRedirectInterfaces.cs`) — a dedicated outcome; **never reuse `Blocked`** for grace drops (executor's `Blocked` path fires `LogProxyUnavailable`, mislabeling grace consumption as proxy failure).
- `FlowTable.RemoveExpired(now, isHeld?)` (`src/WinForward.Core/Domain.cs`) — optional hold predicate; held entries are skipped **without Touch**, so they expire at their original idle point once the hold lapses.

### 3. Contracts

- **Single tombstone write point**: every teardown entry (relay completion, relay failure, fail-closed, global dispose) funnels through `TcpRedirectSessionStore.RemoveAssociationFromTable` — the only caller of `TcpRedirectTable.TryRemove` — and that point also writes the tombstone (grace `TombstoneGracePeriod` = 60 s, defined in `TcpRedirectSessionStore`). Any new teardown path must go through it.
- **Atomic retire (task 08-30-atomic-retire, 2026-08-30)**: `RetireSessionUnderGate` performs the session-dict removal, `Phase = Closing`, retire, table alias removal, AND tombstone arming **inside one store-gate critical section** — all three retire entry points (`TearDownSessionAsync`, sweep `RemoveExpiredAsync`, `DisposeCoreAsync`) are covered by that single method. Disposal (listener → self-traffic token → relay → lifetime CTS) trails *outside* the gate; only the synchronous table+tombstone pair is atomic. Documented lock order: **store gate → table gate → tombstone gate** (verified acyclic repo-wide; table/tombstone critical sections never call upward — do not add a path that does). Session-less release paths (`FailAssociationAsync` no-session branch, `TcpRedirectSetup` disposed-store path) still call the standalone `RemoveAssociationFromTable` — mutually exclusive with retire per association instance, and `TryRemove`'s `ReferenceEquals` guard makes any repeat call (and any port-bitmap double-decrement) a no-op. Without this, a same-tuple SYN in the retire→removal gap completed a handshake with an about-to-be-disposed listener (client saw connect-then-instant-death); the relay-end RST (R1) made the window practically reachable because the client reacts to the RST while the stale alias is still resolvable.
- **Tombstone queue drains on sweep (R3-TCP, same task)**: `RemoveExpired` head-drains `_insertionOrder` while the head is expired *or* stale (dictionary's current entry for the key is a different record — a refresh appends a fresh tail with a new expiry, so queue order ≈ expiry order and head-drain is order-safe). Before this, the queue grew monotonically with total TryAdd calls (days of churn → hundreds of MB) while the dictionaries stayed bounded.
- **Late-packet consumption**: after a forward (`TryResolveByOriginal`) or reverse (`IsReverseCandidate`) miss, the coordinator consults the tombstone (`TcpProxyCoordinator` checks `Tombstones.TryHit`) before falling back — **on the SYN path too** (`HandleSynAsync` checks after resolve-miss, before capacity/`TryClaim`; wired 2026-08-30 with atomic retire — previously a straggler SYN in grace silently opened a fresh redirect instead of consuming the grace). A hit returns `Dropped`; the executor silently consumes (trace `packet.dropped reason=grace`), and the dispatcher maps reverse-straggler `Dropped` to `ProxyConsumed` — **not** `Block` (which would emit `reason=policy`).
- **The `NotRelevant → Pass` fallback remains solely for connections established before capture started** — those packets are data-plane-identical to late packets, so no timestamp can separate them; only the per-flow tombstone expiry can. Baseline smoke: 113 notrelevant pre-fix → 11 (legitimate pre-existing connections) with 55 grace-dropped post-fix.
- **Flow hold**: `TcpProxyCoordinator.HoldsFlow` = has session ∨ has tombstone. Sweeper order is **tcp → flows → udp** (see `IdleExpirySweeper`) so tombstones/sessions are recycled before flows are evaluated; capacity summary stays at tick end. Held flows are not touched, so a silently-idle relaying flow survives flow-idle expiry and resumes without re-evaluation (no `flow.created`).
- **Capacity**: tombstone capacity derives from the same `tcpFlowCapacity` budget at wiring; tombstones occupy neither `_sessions` nor the `TryClaim` gate.
- UDP is deliberately untouched: sessions rebuild directly on expiry (no handshake → no stray-packet rebound); responses have their own reverse branch.

### 4. Validation & Error Matrix

| Condition | Result |
|---|---|
| Late forward/reverse packet within grace | `Dropped`, silent consume, no reinjection to the real server |
| Late packet after grace expiry | falls back `NotRelevant → Pass` (pre-existing-connection semantics) |
| Tombstone table full | evict oldest entry, add new |
| Relay-held flow reaches flow-idle expiry | skipped, no Touch, no re-evaluation on resume |
| Hold lapses (teardown + grace passed) | flow expires at its original idle point |

### 5. Good/Base/Bad Cases

- Good: client's final ACK after relay completion hits the tombstone and is consumed — no RST rebounds from the real server.
- Base: a packet for a connection torn down 90 s ago (grace lapsed) passes as `NotRelevant` — same as pre-capture traffic.
- Bad: reusing `Blocked` for grace drops (mislabels as proxy-unavailable); holding flows by Touching them (defeats original-idle-point expiry).

### 6. Tests Required

- `TcpRedirectTombstoneTableTests`: window hit / evict-oldest / expiry recycle / rewrite-refresh / queue-drain convergence (`RemoveExpiredDrainsStaleQueueRecordsFromRefreshChurn`, `QueueLengthConvergesToLiveEntriesUnderRefreshAndExpiryChurn`).
- Coordinator tests: both-direction straggler `Dropped`; grace-expiry fallback to `NotRelevant`; relay-failure writes tombstone; executor silent consume + trace (and no executor `packet.completed` for grace); `HoldsFlow` phases; flow expiry at original idle point after hold lapses (split clocks); real-sweeper silent-flow survival (no `flow.created`); atomic-retire parking-listener test (`RetireRemovesTableAliasAndArmsTombstoneBeforeListenerDisposalCompletes` — same-tuple SYN mid-teardown must hit the tombstone, never the dying listener).
- Dispatcher regression: reverse-straggler `Dropped` maps to `ProxyConsumed` without a `reason=policy` label.

### 7. Wrong vs Correct

#### Wrong

```csharp
// Grace drop reusing Blocked: executor's Blocked branch logs proxy-unavailable
// and the trace says reason=policy — both mislead diagnosis.
if (tombstone.TryHit(key, now)) return TcpRedirectOutcome.Blocked;
```

#### Correct

```csharp
// Dedicated outcome; executor consumes silently with its own trace reason,
// and the dispatcher maps the reverse-straggler form to ProxyConsumed.
if (Tombstones.TryHit(reverseSource, reverseDestination, now) ||
    Tombstones.TryHit(key, now))
    return TcpRedirectOutcome.Dropped;
```
