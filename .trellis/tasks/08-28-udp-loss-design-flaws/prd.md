# Fix UDP loss design flaws: inline session setup, fragile receive loop, global native gate

## Goal

Eliminate the design flaws in the UDP proxy path that abnormally raise UDP packet loss
(both outbound/on-path loss affecting all traffic on an adapter, and return-path loss
affecting relayed responses). Source: the design review conducted 2026-08-28 (session
opencode_ses_fb70d58e1ffejT3eOvwmKCgk3r), which traced the full chain
capture pump -> dispatcher -> UDP session -> SOCKS5 relay -> response reinjection.

## Background (confirmed facts from review)

All anchors verified against current master:

1. **Inline session setup blocks the capture pump.** The first datagram of every new UDP
   flow is dispatched synchronously on the pump:
   `NdisCapturePump.RunAsync` awaits each handler in order (`src/WinForward.NdisApi/NdisCapture.cs:83`)
   -> `CapturePacketProcessor.ProcessAsync` (`src/WinForward.Runtime/CapturePacketProcessor.cs:66`)
   -> `NdisPacketActionExecutor.HandleUdpProxyAsync` (`src/WinForward.Runtime/NdisPacketActionExecutor.cs:145`)
   -> `UdpProxyCoordinator.TrySendAsync` awaits the full session setup task
   (`src/WinForward.Runtime/UdpProxyCoordinator.cs:90`)
   -> `Socks5ControlConnection.ConnectAsync`: DNS resolution (30s timeout) + up to 4
   connect attempts x 30s (`src/WinForward.Runtime/Socks5ControlConnection.cs:14-15`)
   + auth + UDP ASSOCIATE.
   During setup the pump never calls `TryReadPackets`, so the ndisrd per-adapter bounded
   queue keeps filling; overflow silently drops ALL traffic on that adapter. UDP has no
   retransmit, so loss is permanent. With an unreachable SOCKS5 server the stall is up to
   ~150s per new flow.
   `BoundedSetupQueue` (`src/WinForward.Core/PacketRuntime.cs:147`) exists for
   "buffer while setting up" but is dead code in the UDP path (only tests reference it).
2. **One stray datagram kills the whole receive loop (100% return-path loss).**
   `Socks5UdpTransport.ReceiveAsync` throws on: unexpected relay source port,
   `receivedBytes >= buffer.Length` (1537, `src/WinForward.Runtime/Socks5UdpTransport.cs:150`),
   and SOCKS5 decode failure (`Socks5UdpTransport.cs:132-137`).
   `UdpProxySession.ReceiveLoopAsync` treats any non-cancellation exception as fatal,
   writes `_receiveFailure`, and exits (`src/WinForward.Runtime/UdpProxySession.cs:171-174`),
   tearing down the session. Injection failures thrown by `UdpResponseReinjector`
   (e.g. `Win32Exception` on a vanished adapter) hit the same fatal path. A single
   relay datagram >= 1537 bytes (DNSSEC answers, QUIC bursts, jumbo paths) therefore
   permanently stops response delivery for that flow until the next client datagram
   re-triggers setup (defect 1 again).
3. **One process-wide Monitor serializes all native calls.** `NdisNativeCallGate`
   (`src/WinForward.NdisApi/NdisNativeCallGate.cs:14-24`) is a single `Monitor` shared by
   every batch read, every per-packet pass reinjection, and every UDP response injection.
   Multi-adapter / high-pps workloads funnel all DeviceIoControl calls through one lock;
   one slow IOCTL stalls every pump's reads -> driver queue overflow.
4. **Response path throughput ceiling.** Per session one receive loop drains one datagram
   at a time, each iteration doing a synchronous injection through the global gate; the
   relay socket's receive buffer is not tuned (`ReceiveBufferSize` never set); the
   reinjector allocates a fresh native `NdisPacketBuffer` per response
   (`src/WinForward.Runtime/UdpResponseReinjector.cs:104`, ignores the existing
   `NdisPacketBufferPool`), and `UdpFrameBuilder.TryBuild` allocates a `byte[]` per
   response (`src/WinForward.Protocols/UdpFrameBuilder.cs:62`).
5. **`_sendBuffer` data race (latent).** `Socks5UdpTransport._sendBuffer` is an instance
   field used without serialization (`Socks5UdpTransport.cs:118-123`); the "one flow is
   only ever seen by one pump" invariant is implicit and unenforced. If the same flow key
   is captured on two in-scope adapters, concurrent sends interleave writes and corrupt
   SOCKS5 datagrams, which the relay drops.
6. **Poll granularity.** Empty-queue poll delay is 1ms (`NdisCapture.cs:52`) but
   `Task.Delay(1)` on Windows resolves to ~15.6ms. Latency-only alone; amplifies queue
   overflow risk together with 3/4.
7. (Consequence, no separate work) Relay idle expiry (2min,
   `src/WinForward.Runtime/IdleExpirySweeper.cs:42`) re-triggers defect 1's stall for
   keepalive-style flows; fixed transitively by fixing 1.
8. (Consequence of 2) Inconsistent size behavior: payloads 1515..1526B responses are
   dropped fail-closed with a log, >= 1527B tear the session down. Fixing 2 makes the
   behavior uniform (skip + log).
9. (Deferred candidate) Reverse-path response Pass depends on the flow-table entry still
   holding a Proxy decision; on eviction the response can be re-evaluated as a new flow.
   Edge case, isolated fix possible later.

## Requirements

- R1 (defect 1): New-flow UDP session setup MUST NOT block the capture pump. Datagrams
  arriving while setup is in progress must be either buffered (bounded) or consciously
  dropped with a trace event - never leave the adapter queue to overflow. Bounded setup
  queue semantics must be defined and tested.
- R2 (defect 2): A single malformed / unexpected-source / oversized relay datagram MUST
  NOT terminate the receive loop. Only socket-level failures may tear down a session.
  Oversized-but-decodable datagrams beyond the frame cap are skipped with a
  (rate-limited) log, matching current fail-closed frame-cap behavior for 1515..1526B.
- R3 (defect 3): Native-call serialization must not couple unrelated adapters. Read vs
  reinjection contention must be reduced so a slow IOCTL cannot stall every pump.
- R4 (defect 4): Response path must sustain bursts: explicit relay socket receive buffer
  sizing, pooled native buffers for response injection, no per-response managed
  allocation on the steady path where feasible.
- R5 (defect 5): Concurrent `SendAsync` on one transport must be impossible or safe
  (serialize or per-call buffer).
- R6 (defect 6): Empty-queue pump wait must not round 1ms up to a 15.6ms timer tick.

## Out of Scope

- Defect 9 (flow-table eviction race on reverse responses) - candidate follow-up task.
- Any change to policy/rule semantics, config schema (unless a new knob is strictly
  required by R1/R4 defaults), TCP redirect path behavior.
- Driver-side (ndisrd) changes; queue-depth tuning inside the driver.

## Decisions

- D-Scope (user, 2026-08-28): fix R1–R6 in this task (high + medium severity together).
- D-SetupOverflow (technical, recorded in design D1): bounded setup queue overflows
  drop-oldest with FIFO delivery of retained datagrams; drops are traced and logged.
- D-Deferred: flow-table eviction race on reverse responses (defect 9) — follow-up task
  candidate; batched native send and event-driven pumping (`SetPacketEvent`) recorded as
  future optimizations (design D4/D6).

## Acceptance Criteria

- [ ] AC1: With a SOCKS5 server that delays UDP ASSOCIATE by N seconds, a burst of UDP
  datagrams to a new flow plus concurrent pass-through traffic on the same adapter
  experiences zero driver-queue-induced drops attributable to the stalled pump
  (validated with fakes + a loss-counting test; N >= 5s).
- [ ] AC2: A relay returning one malformed datagram, one unexpected-source datagram, and
  one >= 1537-byte datagram in a stream of valid datagrams: valid datagrams before AND
  after each bad one are still delivered; the session survives (unit test with fake
  transport).
- [ ] AC3: Multi-adapter capture with concurrent read+inject no longer serializes on a
  single lock (verifiable by design review of the gate split + contention test).
- [ ] AC4: Response injection uses pooled native buffers and an explicitly sized relay
  socket receive buffer; steady-state response path allocates no per-datagram managed
  byte[] (benchmark or allocation-test evidence).
- [ ] AC5: Concurrent `TrySendAsync` for the same flow from two simulated pumps cannot
  corrupt the SOCKS5 encode buffer (test or proof of serialization).
- [ ] AC6: Empty-queue wait between reads is measurably <= ~2ms under a traffic generator
  (or replaced by an event-driven/WaitableTimer mechanism with equivalent evidence).
- [ ] AC7: `dotnet test -c Release` green; hot-path spec invariants
  (`.trellis/spec/backend/hot-path.md`) still hold for the steady-state pass/block path.
