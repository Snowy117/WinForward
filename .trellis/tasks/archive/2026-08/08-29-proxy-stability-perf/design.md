# Design — Proxy Stability and Performance Hardening

All paths relative to repo root. Baseline: commit `46cc639`. Each fix is an independent commit
with its own tests; no fix depends on another landing first except where noted (S1 builds on the
RST-infrastructure touches of S4).

## S1 — Fragment handling on associated TCP flows

**Current behavior (to be pinned by characterization tests first):**
- Flow parsers reject fragment bits → frame is non-flow → `DispatchNonFlowAsync` passes it
  (`FlowDispatcher.cs:227-247`). For host shape the passed fragment leaves toward the real
  server, which never saw the proxied connection and answers the unknown tuple with RST —
  cross-talk that then re-enters through the reverse hook. For forwarded shape it exits via
  Windows routing.
- If a fragment ever resolves to an association (defense-in-depth layer), the rewrite fails
  (`PacketChecksums.cs:104-105`) and `FailAssociationAsync`
  (`TcpRedirectSessionStore.cs:258-264`) tears the session down without RST; the 60 s tombstone
  then swallows retransmissions silently.

**Options considered:**
1. Reassemble fragments — rejected: large, risky, out of scope.
2. Pass fragments (status quo) — rejected: violates "never silently pass proxy-selected traffic"
   and creates real-server cross-talk.
3. **Chosen: consume + explicit fail.** Frames carrying fragment bits whose 4-tuple matches an
   association in `TcpRedirectTable` (either direction) are consumed (dropped), the association
   is torn down **with a client-visible RST** (reuse `ClientResetInjector` with the tracked
   `ClientNextSeq`/`ServerNextSeq`; in-window per RFC 5961), and a distinct trace reason
   (`reason=fragment`) is emitted. Fragments that match no association keep today's non-flow
   pass behavior (they cannot be attributed to a proxied flow).

**Design details:**
- Matching happens where the reverse/forward tuple lookups already run
  (`TcpProxyCoordinator.HandlePacketAsync` / `HandleReverseIfApplicableAsync`): on parse failure
  due to fragment bits, attempt a raw-tuple match against the table before falling back to
  non-flow. This requires a raw IPv4 header read (no TCP header needed for the tuple — first
  fragments carry it; non-first fragments carry only the IP tuple, which is exactly enough).
- IPv6 fragment headers (nextHeader 44) already fail parsing; apply the same raw-tuple match on
  the IPv6 addresses from the base header.
- RST injection reuses `TcpResetInjector`/`ClientResetInjector`; when sequences are unknown
  (should not happen for `Relaying` sessions; possible for `Redirecting`), fall back to today's
  silent teardown plus a warn.
- Tests: characterization (current pass/teardown behavior per shape and leg) → then new pinned
  semantics (consumed + RST + trace event + tombstone armed).

## S2 — UDP `SIO_UDP_CONNRESET`

**Chosen:** two layers, both required:
1. Disable the behavior at the source: after creating the relay socket in
   `Socks5UdpTransport.CreateAsync` (before bind), issue
   `socket.IOControl(IOControlCode...SIO_UDP_CONNRESET, BitConverter.GetBytes(0), null)`
   (vendor IOCTL `0x9800000C`, input `FALSE`). Windows-only project → unconditional. Expose via
   a small internal seam (`ISocketControl`-style or virtual method) so tests can assert the call;
   the IOControl itself is smoke-tested on the Windows run.
2. Treat `SocketError.ConnectionReset` in `Socks5UdpTransport.ReceiveAsync` /
   `UdpProxySession.ReceiveLoopAsync` as a skip-class anomaly: count it in the existing
   rate-limited skip summary (`UdpProxySession.cs:236-268`) and continue the loop, mirroring the
   R2 resilience pattern already pinned by `UdpReceiveResilienceTests`.

**Risk:** none identified; layer 2 alone would also fix the churn but costs a wakeup per ICMP;
layer 1 removes the storm. Both are independently testable.

## S3 — Observing relay completion on all paths

**Chosen:** add a private `ObserveCompletion(ITcpRelay)` helper in `TcpRedirectAcceptor` (or on
the relay itself) that hooks `Completion` with a continuation swallowing-and-debug-logging any
exception; call it (a) in `TcpProxyRelay.DisposeAsync` before disposal (`TcpProxyRelay.cs:196-201`),
and (b) on the attach-failure path (`TcpRedirectAcceptor.cs:67-72`) where the relay is discarded.
The existing success-path observer stays as-is (it owns teardown). Tests use a faulting fake
relay to assert observation happens on both paths (no `TaskScheduler.UnobservedTaskException`
timing games).

## S4 — RST on capacity-rejected SYN

**Chosen:** build an RST|ACK directly from the observed SYN frame (no association exists):
source = original server tuple, destination = client, `seq = 0`, `ack = clientISN + 1`,
flags RST|ACK — a client in SYN_SENT receiving this aborts immediately with ECONNREFUSED.
`TcpResetBuilder` gains a SYN-only variant (template MACs/IPs from the SYN, new header values);
host shape injects toward MSTCP, forwarded shape toward the origin adapter (same matrix as
`TcpRedirectInjector`).

**Amplification guard:** inject at most once per 4-tuple per cooldown window (1 s, matching the
UDP setup-cooldown precedent), tracked in a small bounded set (tombstone-table-style: capacity =
session budget, evict-oldest). Retransmitted SYNs inside the window are silently dropped as
today. Rate-limited warn + existing `tcp.redirect.capacity` summary remain.

**Rejected:** answering every retransmission (reflection vector for spoofed sources).

## S5 — Adapter local-address cache

**Chosen:** cache in `WindowsAdapterLocalAddressProvider`, keyed by adapter Id: an immutable
address-list snapshot per adapter, published via `Volatile` read; rebuild on
`NetworkChange.NetworkAddressChanged` (subscription in the provider, alive for the process) plus
a 30 s safety TTL (events can be coalesced). Enumeration failure keeps the last snapshot (stale
beats fail-closed for address *selection*; the no-address → fail-closed path at
`TcpRedirectSetup.cs:80-86` is unchanged when the snapshot is truly empty).

**Seam for tests:** the enumeration call goes behind an internal interface
(`Func<NetworkInterface[]>`-style injectable in tests, mirroring existing TestHelpers fakes);
tests assert steady-state zero enumerations across N SYN setups and refresh-on-event/TTL.

**Note:** `AdapterIdentity.cs:40` also enumerates, but only at startup wiring — out of scope.

## S6 — Observability gaps

- (a) Domain-typed response drop: extend the skip-summary counters in `UdpProxySession` with a
  `domainDestination` reason (`UdpProxySession.cs:180`) — same 5 s rate-limited debug shape.
- (b) Dead-relay warn: route the per-datagram catch in `NdisPacketActionExecutor.cs:158-161`
  through the existing rate-limiter pattern (5 s window).
- (c) Blocked-reason text: thread the actual reason into the warn (`reason=capacity|claim|
  rewrite|...`) instead of the "not initialized in this build" sentence, which stays accurate
  only for the genuine uninitialized case. Structured field, single line.
- (d) Sweeper catch: log a rate-limited warn with the exception type/message
  (`IdleExpirySweeper.cs:86-91`) and keep the swallow-and-retry semantics.

## P1 — Relay CTS churn

**Chosen:** in `TcpProxyRelay.PumpAsync`, allocate one linked CTS per direction per session and
reuse it per operation via `CancellationTokenSource.TryReset()` (re-create when `TryReset`
returns false, i.e. already canceled) + `CancelAfter(StallTimeout)` before each `ReadAsync`/
`WriteAsync`. Semantics preserved: per-op stall window, session lifetime token, and cross-pump
cancellation still cancel the peer pump immediately. Buffers stay 8 KB per direction.

**Alternatives rejected:** socket `Receive/SendTimeout` (conflates stall with teardown, interacts
with the deliberate `Infinite` reset in `Socks5ControlConnection`); `System.IO.Pipelines`
(out of proportion).

**Gate:** `TcpRelayBenchmarks.OneWayAsync` (chunk 1024) must show per-chunk allocation ≈ 0 and
no >5% throughput regression; `TcpProxyRelayTests` (half-close, sibling-cancel) unchanged.

## P2 — Checksum work

**P2a — incremental endpoint rewrite (chosen):** implement RFC 1624 incremental update
(`HC' = ~(~HC + ~m + m')`) over only the changed 16-bit words (IP src/dst, ports) in
`PacketChecksums.TryRewriteIpv4Tcp/TryRewriteIpv6Tcp`, replacing the full-segment recompute.
Bit-exact equivalence is a property: for randomized frames, `incremental(frame) ===
full-recompute(frame)`. Existing `TcpEndpointRewriteTests` (byte-exact roundtrips, 0↔0xFFFF
non-inversion) must pass unchanged.

**P2b — vectorized Internet checksum (conditional):** `Vector128`/`Vector256` pairwise
accumulator + carry fold for the full-compute paths (`UdpFrameBuilder` rebuild, IPv4 header
checksum), scalar fallback below a size threshold. **Land only if** checksum-focused benchmarks
show a meaningful win (>20% on the rebuild path micro); otherwise keep as recorded follow-up.
Identical semantics incl. computed-0 → 0xFFFF for UDP.

**Benchmarks:** add/extend checksum micros in `FrameRewriterBenchmarks`/`ParserBenchmarks`
before optimizing so before/after is measurable in one run under
`benchmarks/results/<date>-proxy-hardening/`.

## Cross-cutting

- **Trace/event additions** (S1 fragment reason, S4 capacity-rst, S6 fields) follow the logging
  spec: single line, metadata only, level discipline (trace for per-packet, info for summaries).
- **New seams** (socket IOControl, interface enumeration, relay observation) stay `internal` +
  `InternalsVisibleTo`-style test access per existing TestHelpers conventions — no public API.
- **No layer changes:** all edits stay inside `WinForward.Runtime`, `WinForward.Protocols`,
  `WinForward.Windows`; no config, no ABI, no wire-format changes.

## Rollout / rollback

Independent commits in this order (each green on the full suite before the next):
`S2 → S6 → S3 → S4 → S1 → S5 → P1 → P2a → (P2b?)`.
Rollback of any fix = revert its commit; no cross-commit coupling except S1 reusing the SYN-RST
variant introduced in S4 (reverting S4 keeps S1 working via the existing tracked-sequence RST).
