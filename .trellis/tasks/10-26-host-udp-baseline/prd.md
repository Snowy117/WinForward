# Host UDP baseline

Parent: `.trellis/tasks/10-26-kernel-host-udp` (umbrella requirements/design/acceptance).
Sibling (batch 2, later): `10-26-host-udp-local-uoT`.

## Goal

Deliver the first real Kernel-provider host UDP path: a dual-mode WFP listener reads the original
destination per datagram, the existing `UdpProxyCoordinator` carries the flow through a SOCKS5 UDP
ASSOCIATE, and replies leave through the receiving listener socket so the client sees its original
destination. Add pass/block, capacity/idle, the cmsg/flow tests and the Windows E2E baseline.

**Outcome:** the reply-source half of this goal was disproven — the listener `sendto` reply exposes
the listener endpoint, not the original destination — so the batch closes deferred behind the UDP
capability gate and keeps the listener/coordinator work as scaffolding. See "Deferred completion".

## Deferred completion (2026-10-26)

The batch closes **deferred**: the listener/coordinator scaffolding stays implemented, but the host UDP
proxy is disabled end to end, so no UDP association is proxied and no host UDP support is claimed.

- **Capability gate.** `ConfigurationKernelCapabilities.ValidateHostRules` refuses every host `proxy`
  rule whose `Protocol` selector can match UDP (absent or containing `udp`) at
  `WinForward.Host.Rules[i].Protocol`. The message states that the WinForward.Kernel host UDP proxy is
  not available yet because the listener reply cannot preserve the original destination as its source
  (an unconnected client would see the proxy listener), that the driver-side reply injection is
  pending, and that UDP needs `Provider = WinpkFilter`. Pass/block rules may still match UDP. The
  non-default `UdpSessionCapacity`/`UdpRelayReceiveBufferKb`/`UdpSessionIdleSeconds` inert warnings are
  restored (the host UDP coordinator is not started).
- **Data path off.** `KernelHostService.HostUdpProxyEnabled = false` (with a TODO naming the constraint
  and the host UDP spec section) keeps the receive leg off: the UDP service/coordinator stay
  constructed so the composition and disposal stay wired, and the listener-bound/ready/started lines
  say the host UDP proxy is disabled. The `KernelBackend` UDP pools/executor/clock stay as scaffolding
  with a TODO.
- **Scaffolding documented.** `HostUdpResponseSink`/`KernelHostUdpService` say the listener `sendto`
  reply exposes the listener endpoint (A4 failed), that they are not on the product path, and that the
  driver reply-source injection replaces the sink.
- **Docs state the deferral.** README and the host UDP section of
  `.trellis/spec/backend/kernel/wfp-architecture.md` record the disabled proxy, the reason, and the
  WinpkFilter pointer.

The reply-source child `10-26-host-udp-reply-source` owns the driver-side injection and the A4/A8
rerun; when it passes, the gate lifts, `HostUdpProxyEnabled` flips, and this batch's listener/coordinator
code is committed with it. Evidence: `verification/a4-failure.md` (listener-source reply), the A8
connected sample in `verification/a8-connected.txt`, and `verification/deferred.md` (this closure).

## Requirements

- **B1 Configuration.** Kernel host proxy rules may match UDP (explicit `Protocol:["udp"]` or absent
  `Protocol`). The host adapter/forwarded/path gates stay. `UdpSessionCapacity`,
  `UdpRelayReceiveBufferBytes` and `UdpSessionIdleTimeout` become live; their inert warnings go.
  Local-target rules remain rejected in this batch.
  **Deferred:** the UDP-proxy part of B1 is not shipped; the gate and the inert warnings stayed until
  `10-26-host-udp-reply-source` passes.
- **B2 Listener.** Bind one `HostListenerSet`: v4 TCP + v6 TCP (V6ONLY) + one dual-mode v6 UDP
  wildcard on the same port; enable the WFP redirect-context option (levels 0/41) and disable
  `SIO_UDP_CONNRESET` before bind. Receive with `WSARecvMsg`; extract cmsg type 70/level 0 or 41 and
  decode/validate the 36-byte payload with `RedirectDestination.TryParse` (not a second ABI
  decoder). The cmsg level and context family are independent (level 41 can carry family 4 after
  the 5f normalization).
- **B3 Host service.** Validate every datagram; normalize a v4-mapped client endpoint to IPv4 when
  the context family is 4; build an adapterless host `FlowKey`; length-check the 4096-byte cap
  **before** coordinator admission; resolve the `rule_index` target; call the shared coordinator and
  contain a per-datagram exception or `false` admission so it cannot kill the receive loop or tear
  down a live session. Missing/invalid context, non-UDP context, out-of-range rule, non-proxy/
  unknown target, family mismatch and over-cap datagrams are fail-closed drops counted as
  `udpFailClosedDrop` / `kernel.udp.dropped`; no session is created. Replies go via the receiving
  socket; an IPv4 `Flow.Local` is mapped back to `::ffff:` for the AF_INET6 listener.
- **B4 Shared engine.** Reuse `UdpProxyCoordinator`/`UdpProxySession`/`UdpSessionSetup`, the two-tier
  idle sweep and the SOCKS5/local transports. Add the host sink/core composition overload; the
  packet path's behavior is unchanged. The host service ticks the shared activity clock and runs
  the idle sweep without a packet `FlowDispatcher`.
- **B5 Lifecycle.** Extract a `KernelHostService` orchestrator (ABI/disable stale/compile/bind/start
  UDP/publish/config/start TCP/ready) and reduce `KernelHostTcpService` to the TCP accept/relay and
  capacity path. Preserve the verified TCP start order. Shutdown: disable config → cancel → dispose
  the listener set (closes the UDP socket, unblocking `WSARecvMsg`) → await UDP join/coordinator
  dispose → await TCP drain → dispose block sink → clear forwarded. The listener set is the single
  socket owner; `ShutdownFaulted` lives on the orchestrator. A failed start leaves the driver
  disabled.
- **B6 Observability.** Listener-bound/ready/stop lines say whether UDP is enabled and its session
  capacity; a rate-limited drop line names the reason; existing UDP `RuntimeCounters` are used, with
  `kernel.udp.accepted/dropped/active` where useful. No full heartbeat section.
- **B7 Tests/E2E.** Unit tests for the cmsg extraction/`RedirectDestination` decode,
  client-endpoint normalization, flow/target resolution, the sink (including the IPv4→`::ffff:`
  mapping) and the config gate (the existing UDP-gate/warning cases are updated by name). Linux/root
  gates and the Windows driver gate stay green. The VM E2E A1–A5, A8 and A10 (parent design §9)
  runs with the test-signed driver, sing-box and three labeled Linux UDP targets; A1 sends three
  datagrams, A3 checks the host stop line's block-sink count, and A5's post-expiry client uses a
  new source port. The evidence is recorded and the VM cleaned.
- **B8 Boundaries.** No driver/C++ change, no ABI/context change, no local target/DNS or UoT, no
  forwarded change, no default-provider change, no second UDP proxy implementation, no workaround
  for a failed A4/A8.

## Acceptance criteria

> **Deferred:** with the host UDP proxy disabled, the UDP data-path criteria are not met by this batch
> alone. B-AC1's UDP acceptance, B-AC2 (A1/A4), B-AC3 and B-AC4 (A8/A10) transfer to
> `10-26-host-udp-reply-source`; the current TCP-path and gate status is recorded in
> `verification/deferred.md`. Nothing here claims host UDP proxy support.

- [ ] B-AC1: `validate` accepts a Kernel UDP proxy config; a TCP-only config still behaves as
      before; the local-target rejection remains with an actionable diagnostic; UDP settings are
      live.
- [ ] B-AC2: A1/A4 — a UDP client sends three datagrams through the SOCKS5 target, receives all
      replies, and its unconnected `ReceiveFrom` source equals `192.168.100.4:40010`; sing-box
      shows the relay; driver `udp_redirected`/`udp_classify_calls` grow by exactly one association.
- [ ] B-AC3: A2 pass reaches the target directly with no sing-box flow; A3 block is a silent
      drop/timeout with no target traffic, `udp_blocked/deferred_blocks` growth and a zero host
      block-sink receive count; A5 capacity is fail-closed and an expired session is rebuilt by a
      new source-port client with a new classify.
- [ ] B-AC4: A8 — native IPv4, v4-mapped connected and native IPv6 flows work through the dual-mode
      listener; A10 — a local datagram without a redirect context is dropped without a session or a
      crash; TCP and UDP share the listener port.
- [ ] B-AC5: The refactored TCP Kernel path still passes the 5f TCP E2E (proxy/pass/block); the
      Windows gate, root format/build/test and Kernel `check.sh --full` are green; the stale
      “host UDP relay” diagnostics are gone and their pinned tests updated.
- [ ] B-AC6: The evidence (clients, source endpoints, driver stats, sing-box, target, startup,
      cleanup) is under `verification/`; no ABI change, no unrelated dirty files; no commit before
      the evidence is complete.

## Out of scope

Local target/DNS, EDNS0, UoT v2, connected-leak release/mitigation, full heartbeat parity, forwarded
UDP, rule hot reload, old Kernel scaffolding retirement, and any C++/driver change.

## Decisions

Inherit the parent HU-D1–HU-D10. This batch uses the 4096-byte cap, accepts the connected-UDP leak,
uses the dual-mode host listener, normalizes the v4-mapped client endpoint, reuses the coordinator,
and keeps WinpkFilter as the default.

## Risks

- A4 reply source and A8 dual-mode delivery are blocking; stop and revisit the design if either
  fails.
- The `KernelHostService` refactor must not regress the 5f TCP order; re-run the TCP E2E.
- The UDP receive loop must not be terminated by `WSAECONNRESET`; the sink and listener must be
  disposed in the right order.
- The 4096 cap must not silently break DNS; batch 2 proves EDNS0.
