# Fix host UDP reinjection delivery failure

## Goal

Host-originated UDP flows that WinForward proxies through a SOCKS5 relay frequently fail to
deliver the relay response back to the originating client socket (e.g. `q -s plain://1.2.3.4`
times out), while TCP flows, forwarded (VM) UDP flows, and *some* host UDP attempts succeed.
Diagnose the true root cause on real hardware, fix it, and make future diagnosis possible by
adding timestamps to the runtime log.

## Background (evidence gathered so far)

- Environment: Windows host 192.168.100.1 / 192.168.3.114 (WLAN, main LAN), 11-12 adapters in
  capture scope, sing-box SOCKS5 mixed inbound on 127.0.0.1:30890, TUN adapter "Meta" exists
  only when sing-box TUN mode is enabled.
- sing-box side is healthy: the proxied UDP DNS query arrives at the mixed inbound
  (`inbound packet connection to 1.2.3.4:53`), is hijack-dns'd, and a reply is produced and
  sent back toward WinForward's relay socket.
- WinForward trace logs show the full relay path completing for BOTH successful and failed
  queries: `udp.packet.sent` → `udp.packet.received` → `udp.response.reinjected target=mstcp`.
  So the reinjection call executes; the frame simply never reaches the client socket on failed
  attempts (q times out).
- Failure is intermittent per-flow: 12:55:28 fail (TUN off), 12:56:45 fail (TUN on), 12:56:57
  success (TUN on, new source port / new flow). Each q invocation uses a fresh source port,
  so "first attempt after service start fails, later attempts succeed" is the observed pattern,
  independent of TUN state.
- Program.cs:293 `var hostAdapter = scope[0]` picks the reinjection adapter for host flows.
  `scope` is ordered by StableId (lowercase GUID string). Observed adapter set: with TUN off,
  scope[0] = `{0D8855B4-...}` (本地连接 2); with TUN on, scope[0] = `{0AFD3ED9-...}` (Meta/TUN).
  The q query packets are captured on WLAN `{164AEF69-...}`. So host UDP responses are always
  reinjected toward MSTCP on an adapter that is NOT the flow's adapter — yet some attempts
  still succeed, which the current investigation has not explained (weak-host delivery?
  correlation with relay socket vs capture adapter? timing?).
- The runtime log has no timestamps, which made correlating the two service log segments with
  the user's test timestamps manual and error-prone.

## Requirements

### R1 — Timestamped runtime logging (enabler, do first)

- Every runtime log line (info/warn/debug/trace events) carries wall-clock timestamp info so
  field logs can be correlated with external events (client tools, sing-box logs, packet
  captures). Keep the existing plain-text sink; do not introduce a logging framework.
- Must not break existing log parsing expectations documented in README ("Event vocabulary"
  section) beyond the additive timestamp field.

### R2 — Root-cause the host UDP response delivery failure

- Reproduce on the remote test machine (192.168.100.2 via evil-winrm-py) with a controllable
  multi-adapter Windows environment: WinForward + a SOCKS5 UDP-capable server.
- Determine experimentally where the reinjected frame dies for a failing flow (driver call
  return value? wrong adapter weak-host drop? MSTCP peer validation? checksum? Ethernet
  header?) — capture with pktmon or equivalent if needed.
- The explanation must cover ALL observations: first-attempt-fails/later-attempts-succeed
  per-flow intermittency, independence from TUN state, forwarded flows always working, TCP
  host flows working.

### R3 — Fix

- Host UDP responses must be reinjected such that they reliably reach the client socket that
  sent the query (expected: use the flow's own capture adapter, `FlowKey.OriginAdapterId`,
  the same information forwarded flows already use).
- No regression for forwarded UDP flows, TCP redirect flows, or pass/block paths.
- Loop-prevention (design §10) must stay intact: reinjected responses observed again by the
  capture path must still be passed, not re-proxied.

### R4 — Secondary observation (nice-to-have, separate commit at most)

- sing-box's own UDP flows intermittently lose process attribution (`process not found`) and
  fall into the catch-all proxy rule, then fail with "Unable to connect to the configured
  SOCKS5 server" (log line 208 of the old smoke log). If root-causing R2 touches the process
  lookup path, capture findings; otherwise document for a future task.

## Acceptance Criteria

- [ ] Runtime log lines include timestamps; verified by running the CLI locally (unit-testable
      renderer) — existing tests updated, not deleted.
- [ ] A reproducible experiment on the test machine demonstrates the failing delivery path and
      identifies the exact drop point (documented in the task research notes).
- [ ] After the fix, repeated `q A AAAA google.com -s plain://<upstream>` style queries through
      WinForward succeed reliably (≥20/20) from the host, including the first attempt after
      service start, with TUN off (scope[0] ≠ flow adapter) and with multi-adapter capture.
- [ ] Forwarded (VM-side) UDP and host TCP proxy paths still pass their existing tests.
- [ ] `dotnet build` + full test suite green.

## Constraints

- Windows-only runtime code; development happens on Linux (build/test via existing CI setup,
  hardware experiments via evil-winrm-py to 192.168.100.2).
- Do not change sing-box or its config as part of the fix; WinForward must work against any
  SOCKS5 server.
- Fail-closed philosophy of the codebase must be preserved.
