# Kernel provider host UDP

Parent: `.trellis/tasks/10-25-kernel-backend-integration`
Children (batch map):

- `10-26-host-udp-baseline` — listener/cmsg, SOCKS5 UDP ASSOCIATE, pass/block, capacity/idle, E2E A1–A5/A8.
- `10-26-host-udp-local-uoT` — local target/DNS, EDNS0, UoT v2, E2E A6/A7 (created after the baseline).

This umbrella task owns the requirements, cross-batch design and final acceptance for the Host UDP
milestone. It is not the implementation target; the children are.

## Goal

Make the Kernel provider serve host UDP through the same `WinForward.Runtime` UDP engine the
WinpkFilter provider uses: the WFP driver redirects a UDP association to the CLI listener, the
listener reads the original destination per datagram from the WFP redirect context, the shared
coordinator opens a per-flow SOCKS5 or local-target transport, and replies leave through the same
listener socket so the client observes its original destination as the source.

## User value

- One UDP proxy implementation and one session model for both editions, so capacity, idle, setup
  queue, target selection and failure semantics cannot drift.
- Host UDP parity for the common real workloads: direct UDP proxy, DNS-shaped local targets, UoT,
  pass and block.
- The Kernel provider becomes usable for applications that mix TCP and UDP without falling back to
  WinpkFilter.

## Confirmed facts

- The driver has no separate UDP layer: `ALE_CONNECT_REDIRECT_V4/V6` classifies once per UDP
  association (client endpoint + destination), creates one 36-byte redirect context, and redirects
  the association to the shared `listener_port`. Capacity and lifecycle are per association, not per
  datagram. See `research/host-udp-driver.md` §1.
- The listener receives each datagram with the original destination in
  `IP_WFP_REDIRECT_CONTEXT` / `IPV6_WFP_REDIRECT_CONTEXT` cmsg (type 70, level 0/41), readable only
  through `WSARecvMsg`. The context contains protocol 17, family, original address/port, rule index
  and client PID. See `research/host-udp-driver.md` §1.3/§2.
- Replies must be sent from the socket that received the datagram; no new socket and no driver
  injection. A new socket is a new association and changes the source observed by the client. See
  `research/host-udp-driver.md` §3.
- UDP block is enforced by the fixed ALE_AUTH_CONNECT sink filters: send succeeds, the peer receives
  nothing, receive times out. This is the accepted UDP edition difference; it is not an error.
- The existing `UdpProxyCoordinator`/`UdpProxySession`/setup queue/capacity/idle/teardown/skip
  classification and the SOCKS5 UDP, UoT and local-target transports are packet-shape independent.
  Only `UdpResponseReinjector` and `UdpAdapterTargetSource` are packet-specific. See
  `research/host-udp-userland.md` §1.
- `ListenerSet` already binds TCP and UDP on the same port but its UDP sockets are V6ONLY. The WFP
  suite uses a single dual-mode v6 UDP socket and covers native v4, v4-mapped connected and native
  v6. The product listener shape must be verified (A8). See `research/host-udp-driver.md` §5.1.
- Windows 11 26100 leaks the redirect record of a connected UDP association per datagram until
  reboot. There is no safe driver-side release; the existing spec records the evidence. See
  `research/host-udp-seams.md` §8.
- The current config gate rejects any Kernel host proxy rule whose protocol can match UDP, and the
  compiler/runtime reject local targets. These are the host UDP gates to lift. See
  `research/host-udp-userland.md` §4.2.

## Requirements

### HU-1 Listener and context

- Bind the host listener on the same port as TCP: v4 TCP + v6 TCP (V6ONLY) + one dual-mode v6 UDP
  wildcard socket. Enable `IP_WFP_REDIRECT_CONTEXT` (IPv4 and IPv6 levels) before bind and disable
  `SIO_UDP_CONNRESET`.
- Receive with `WSARecvMsg`; parse cmsg type 70/level 0 or 41, extract the 36-byte payload and
  decode/validate it with the existing `RedirectDestination.TryParse` (magic/version/family/protocol/
  port). The cmsg level and the context family are independent: level 41 can carry family 4 after
  the v4-mapped normalization.
- Build a host `FlowKey` from the client endpoint and the context's original destination, with
  `FlowOriginKind.Host` and the adapter-less slot. If the context family is IPv4 and the received
  source is IPv4-mapped, normalize the client endpoint to IPv4 before key creation (the two
  endpoints must share a family); map it back to `::ffff:` only on the reply send. If the families
  still differ, drop the datagram.
- A datagram without a valid redirect context, a context whose protocol is not UDP, an out-of-range
  rule index, a non-proxy target or an unknown target is fail-closed dropped and counted. The
  listener port is reachable by any local process, so this path must not create sessions.

### HU-2 Shared UDP engine

- Reuse `UdpProxyCoordinator`, `UdpProxySession`, `UdpSessionSetup`, the setup queue/cooldown, the
  two-tier idle sweep and the existing SOCKS5/UoT/local-target transports. No second UDP proxy
  implementation.
- Implement the host response leg as an `IUdpResponseSink`. The provisional listener `sendto`
  (mapping an IPv4 `Flow.Local` back to `::ffff:`) was implemented and **failed A4**: the client
  sees the listener source, not the original destination. The accepted response leg must be a
  driver injection or a controlled source rewrite with a new control IOCTL/ABI, delivered by the
  `host-udp-reply-source` child. The baseline listener/coordinator code is not accepted until that
  child passes A4/A8.
- Add the minimal composition seam so the coordinator can be built with a host sink instead of the
  packet reinjector. Keep the packet composition behavior unchanged.
- Tick the shared `ActivityBucketClock` and run the idle sweep from the UDP service; do not require
  a packet `FlowDispatcher`.
- Host datagram cap: 4096 bytes. The service length-checks each datagram **before** coordinator
  admission and drops/contains an over-cap or transport-rejected datagram without tearing down the
  session or the receive loop (the transport's `Oversized` path is receive-side only). The existing
  `UdpSessionCapacity` (16384), `UdpRelayReceiveBufferBytes` (64 KiB) and `UdpSessionIdleTimeout`
  become live under the Kernel provider.

### HU-3 Configuration and targets

- Kernel host proxy rules may match UDP (explicit `Protocol: ["udp"]` or absent `Protocol`). The
  TCP path remains unchanged for rules that also match TCP.
- A local-target rule is allowed only for UDP by the existing config validation; the Kernel UDP
  target resolver accepts it, while the TCP resolver keeps rejecting it fail-closed. The stale
  “host UDP relay is not served” diagnostics in the compiler/TCP resolver are rewritten and their
  pinned test is updated; the batch-1 rejection remains explicit and actionable.
- The existing `rule_index` → configured rule → `ProxyTarget` contract remains; one config rule is
  one blob rule. The fallback sentinel is fail-closed dropped, never a default target.
- `UdpSessionCapacity`, relay receive buffer and idle timeout are no longer inert under Kernel; the
  config warnings for those settings are removed.

### HU-4 Local target/DNS and UoT

- A local target is a direct UDP relay to the configured IP literal, including the DNS-shaped
  `Protocol: ["udp"]`, `RemotePort: ["53"]` configuration. EDNS0 responses larger than 512 bytes
  must survive the 4096-byte host cap.
- A SOCKS5 target with UoT enabled uses the existing UoT v2 transport. The selected transport must
  appear in the target/transport diagnostics.

### HU-5 Observability and lifecycle

- The Kernel ready log/state expresses whether host UDP is enabled and its session capacity.
- Use the existing `RuntimeCounters` UDP counters for capacity, setup rejection/failure, association
  loss, fail-closed drops and response-source mismatch; add Kernel-specific accepted/dropped/active
  counters only where the TCP backend already has a `kernel.*` analog.
- The connected-UDP leak is accepted and documented: 36-byte `WfPr` plus the `Arcl` record per
  connected association is not released until reboot; the E2E samples `WfPr`/`Arcl` and the
  operations docs say so. No driver-side free and no automatic restart.
- Shutdown disables the driver config first; the listener set is the single socket owner, so it
  closes the UDP socket (unblocking `WSARecvMsg`) and TCP listeners, then the UDP service joins and
  disposes the coordinator, then the TCP drain completes and the block sink is disposed. A failed
  start leaves the driver disabled.

### HU-6 Non-regression and parity

- The WinpkFilter provider behavior, default provider, TCP Kernel path, forwarded path and existing
  driver ABI (`probe_config`, control ABI 8, context v1, 36 bytes) are unchanged.
- The shared UDP engine remains the only UDP proxy implementation; the retired Kernel
  `ForwardedUdpRelay`/`Socks5UdpAssociation` scaffolding is not used by the host path.
- The Windows driver gate (`udp-rules` U1–U13, U10) stays green; the new host UDP E2E runs on the
  VM with the test-signed driver and a real SOCKS5 target/echo/DNS target.

## Decisions

| # | Decision |
|---|---|
| HU-D1 | Host UDP datagram cap is 4096 bytes; the existing `UdpSessionCapacity` default stays 16384. `Oversized` drops are counted. Rationale: covers EDNS0/common DNS and most UDP workloads while bounding the worst-case receive-window pool to ~64 MiB at the default capacity; a future config key can raise it. |
| HU-D2 | The Windows 11 26100 connected-UDP redirect-record leak is accepted, documented and sampled; no driver-side release, no automatic restart. |
| HU-D3 | The baseline batch delivers SOCKS5 UDP proxy/pass/block/capacity/idle; local target/DNS and UoT are the next child batch, before the umbrella is accepted. |
| HU-D4 | The baseline adds ready/drop/refusal events and uses the existing UDP counters; a full heartbeat parity section is a later observability batch. |
| HU-D5 | The host UDP listener is a single dual-mode v6 wildcard socket sharing the TCP listener port; native v4, v4-mapped connected and native v6 are verified by E2E A8. |
| HU-D6 | A v4-mapped client endpoint is normalized to IPv4 when the redirect context family is 4, before `FlowKey.Create`; the reply uses the pure IPv4 endpoint from the dual-mode socket. |
| HU-D7 | Reuse `UdpProxyCoordinator`; add only the host sink/composition/sweep/listener seams. No second UDP engine. |
| HU-D8 | No ABI/context change: control ABI 8, context v1/36 bytes, `listener_port` and `block_sink_port` shared by TCP/UDP. |
| HU-D9 | `WinForward.BackendDriver.Provider` remains opt-in; WinpkFilter stays the default until the whole goal's parity/productization gates pass. |
| HU-D10 | UDP block remains silent drop/timeout, the accepted edition difference; no attempt to synthesize ICMP or a reset. |
| HU-D11 | The host UDP reply source must be the original destination. The listener `sendto` path does not preserve it (A4 failed) and userland raw source spoofing does not work on Windows. The response leg must be driver-injected/source-rewritten via a new control IOCTL/ABI; the baseline is not accepted until the `host-udp-reply-source` child passes A4/A8. |

## Acceptance criteria

- [ ] HU-AC1: `validate` accepts Kernel host proxy rules that can match UDP and (batch 2) local
      target rules; the host adapter/forwarded/path gates remain; TCP-only configs behave as before.
- [ ] HU-AC2: A UDP association to a SOCKS5 proxy target reaches the configured target through the
      shared engine; the client receives the reply from its original destination; the driver
      counters and sing-box target evidence are recorded. **The baseline listener `sendto` failed
      this (client saw the listener); the reply-source child must pass A4/A8 before the milestone is
      accepted.**
- [ ] HU-AC3: UDP pass, block, capacity refusal and idle expiry behave as specified: pass reaches the
      target, block is a silent drop with no target traffic and a zero block-sink receive count,
      capacity is fail-closed, and an expired session is rebuilt by a datagram from a **new client
      source port** (which also reclassifies the association).
- [ ] HU-AC4: A local-target DNS-shaped flow (including an EDNS0 response >512 bytes) and a UoT v2
      flow complete with the correct reply source and transport evidence.
- [ ] HU-AC5: Native IPv4, v4-mapped connected and native IPv6 host UDP flows work through the
      dual-mode listener; a datagram without a redirect context is dropped without a session or a
      crash; TCP and UDP share the listener port.
- [ ] HU-AC6: The Windows driver gate and the host UDP E2E pass on the VM; the root .NET and native
      gates stay green; no ABI change; no second UDP proxy implementation; WinpkFilter regression is
      green.
- [ ] HU-AC7: The connected-UDP leak acceptance/operations note, spec updates, verification evidence
      and VM cleanup are recorded.

## Out of scope

- Forwarded UDP/forwarded parity, rule hot reload, full IPv6 address matrices, connected-context
  release assertions, a full heartbeat parity section, and retiring the old Kernel
  `ForwardedUdpRelay`/`Socks5UdpAssociation` scaffolding (that belongs to the forwarded/cleanup
  milestone).
- Any change to the default provider, the ABI, or the WinpkFilter UDP path.
- A new configurable datagram-size key in this milestone (the 4096 cap is a constant).

## Constraints

- One UDP engine; the host path must not fork the coordinator/session semantics.
- Reply source must be the original destination; if A4 disproves the WFP assumption, stop and
  revisit the design instead of masking it.
- `WinForward.Kernel.Runtime` must not reference `WinForward.Runtime`; the host service/sink live in
  the CLI, the platform-neutral parser may live in Kernel.Runtime.
- Every Windows build/cleanup follows the existing test-signed driver and scheduled-task procedures.
- No unrelated dirty files; the pre-existing `.trellis/tasks/08-30-...`, `.trellis/tasks/10-10-...`
  and `benchmarks/results/...` paths stay untouched.
