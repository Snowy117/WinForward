# Host UDP reply source

Parent: `.trellis/tasks/10-26-kernel-host-udp`. Baseline: `10-26-host-udp-baseline` (implemented,
uncommitted, A4 failed). This task owns the response leg that makes a proxied host UDP reply arrive
at the client with the **original destination** as its source.

## Goal

Preserve the client-visible UDP source across the Kernel host UDP redirect: a client that sent to
`Remote` must receive the relayed reply from `Remote`, for unconnected and connected sockets,
IPv4, dual-mode/v4-mapped and native IPv6. The baseline listener/coordinator/SOCKS5 path is reused;
only the response leg changes.

## Confirmed facts

- The baseline listener `sendto` reply arrives with the listener source
  (`192.168.100.2:<listener-port>`), not the original destination. A4 failed; see
  `../10-26-host-udp-baseline/verification/a4-failure.md`.
- Userland raw IPv4 source spoofing works for a local source but not for the remote original
  destination, with the Windows firewall on or off. It is not a viable response leg.
- The WinpkFilter packet path preserves the source by building/injecting a packet; the Kernel host
  UDP redirect has no userland return-source mapping.
- The Kernel driver already has WFP packet-building/injection code for the forwarded path and a
  control IOCTL surface; the host response can potentially reuse these building blocks.
- The current `IUdpResponseSink` seam is one method; the coordinator/session/transports are
  independent of the response mechanics. Replacing the sink must not fork the UDP engine.

## Requirements

- **R1 Driver-side.** The accepted response leg is driver-side (WFP injection or a controlled
  source rewrite). No raw-socket spoofing, no WinpkFilter dependency, no second proxy engine.
- **R2 Source contract.** The client-visible source address and port equal the redirect context's
  original destination. The payload is the relay response unchanged. The reply must be delivered
  once (no duplicate injected/forwarded copy) and must not create a new host UDP association that
  re-enters the redirect.
- **R3 Families and socket forms.** Unconnected and connected IPv4; v4-mapped connected (V6 layer,
  context family 4); native IPv6. The A4/A8 matrix must pass with the Windows firewall in its
  normal (enabled) state; document any required rule.
- **R4 ABI/IOCTL.** Any new request carries a magic/version, address family, client endpoint,
  original destination, payload length and bounds; the control ABI version and the C#/C++ layout
  tests are updated in lockstep. No silent truncation; invalid requests fail closed and are
  counted.
- **R5 Lifecycle/concurrency.** The injection path is safe at the driver's IRQL, asynchronous
  completion is handled, buffers are freed on every path, and shutdown cannot inject after the
  device/association is gone. No double free/UAF; pool tags are accounted.
- **R6 Observability/failure.** Injection failures are counted/logged and fail closed (no direct
  fallback); the E2E can distinguish "relay received the response" from "client received a
  correctly sourced response".
- **R7 Tests/gates.** Host C++ tests for the new ABI/packet construction; Linux tests for the
  userland request/sink logic; Windows driver gate (PREfast/CodeQL/mutations as applicable);
  A4/A8 E2E including connected/unconnected and native IPv6; the existing TCP path and WinpkFilter
  remain green.

## Acceptance criteria

- [ ] RS-AC1: A4 unconnected IPv4 proxy reply source is exactly the original destination; A8
      covers mapped-connected and native IPv6; the Windows firewall stays enabled.
- [ ] RS-AC2: The reply is delivered once, with the correct payload, and does not re-enter the
      host UDP classify (no redirect loop, no self-traffic leak).
- [ ] RS-AC3: The new IOCTL/ABI is versioned and layout-tested; malformed/oversized/unknown-family
      requests are rejected and counted; buffers/pool tags are clean after the suites.
- [ ] RS-AC4: The baseline's listener/coordinator/SOCKS5/pass/block/capacity/idle behavior is
      unchanged; TCP Kernel and WinpkFilter regressions are green.
- [ ] RS-AC5: The Windows gate and A4/A8 E2E evidence are recorded; the baseline code is committed
      only together with this response leg (or the baseline remains behind the UDP capability gate).

## Decisions

- **RS-D1:** Driver-side response; the exact mechanism (transport receive injection vs controlled
  source rewrite vs WFP redirect records) is the research question and must be justified with
  documented WFP behavior and a VM prototype.
- **RS-D2:** The userland `IUdpResponseSink` remains the seam; the new sink sends the payload/endpoints
  to the driver. The coordinator/session/transports are unchanged.
- **RS-D3:** The ABI version bumps if a new request is required; driver and userland ship in
  lockstep. The existing host TCP ABI behavior is unchanged.
- **RS-D4:** The baseline config gate stays removed only when the combined A4/A8 acceptance passes;
  otherwise the UDP proxy capability gate stays fail-closed.

## Out of scope

Local target/DNS and UoT (the sibling branch), forwarded, full heartbeat, and a general raw packet
API. The new driver request is limited to host UDP response injection for an existing redirected
association.

## Open research questions

- Which WFP layer/API can inject a local UDP receive packet with a spoofed source, and what are the
  packet-construction, interface, checksum, firewall and process-attribution constraints?
- Does a documented redirect/return-record mechanism already preserve the source, avoiding a new
  injection path?
- What is the smallest safe ABI request and userland sink, and how is the association identity /
  original destination supplied without trusting the caller?
- Can the forwarded injection helpers be reused, or is a new host response path required?
