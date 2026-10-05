# WinForward

Windows-only, CLI-only transparent network proxy. WinForward intercepts TCP/UDP traffic
through the Windows Packet Filter driver (WinpkFilter/NDISAPI), evaluates ordered rules,
and routes matching flows through configured SOCKS5 servers — a ProxiFyre/socksify-style
transparent proxy for applications that cannot proxy themselves and for traffic arriving
through a selected network adapter (for example a Hyper-V virtual adapter).

## Requirements

- Windows 10 22H2 x64, Windows 11 x64, or Windows Server 2022 or later x64.
- The WinpkFilter driver (`ndisrd.sys`) must be installed and running.
- A matching `ndisapi.dll` (x64) sidecar next to the executable (the pinned release is
  recorded in `src/WinForward.NdisApi/NdisApiAbi.cs`).
- An elevated (Administrator) token is required. WinForward detects a non-elevated launch
  before opening the driver and exits with an actionable diagnostic.

## Build and Publish

Requires the .NET 10 SDK (see `global.json`).

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64
```

The publish output is a single native AOT executable (`WinForward.exe`, no managed runtime
dependency). Copy the matching `ndisapi.dll` (x64) next to it. WinForward loads the DLL from
its own application directory only (not an uncontrolled PATH/current-directory search) and
diagnoses missing DLL, missing export, wrong architecture, inaccessible driver, and
incompatible version separately at startup.

## Commands

```
WinForward validate --config <path>     Validate a configuration file (no driver needed).
WinForward adapters                      List MSTCP-bound adapters (stable ID + friendly name).
WinForward run --config <path>           Intercept and proxy according to the configuration.
```

Exit codes: `0` clean success, `1` configuration/validation/adapter/driver error,
`2` usage or unsupported platform, `3` fatal runtime failure (adapter modes restored first).

`run` stays in the foreground. Press Ctrl+C (or send console termination) for a graceful
shutdown: capture stops, queued packets get a terminal disposition, proxy flows close, and
the original adapter modes are restored exactly.

## Configuration

JSON, rejected on any unknown property. The top level is:

```json
{
  "socks5Servers": [
    { "name": "main", "host": "proxy.example.com", "port": 1080,
      "username": "user", "password": "secret" }
  ],
  "host": {
    "fallbackAction": "pass",
    "rules": [
      {
        "process": ["browser.exe"],
        "protocol": ["tcp", "udp"],
        "addressFamily": ["ipv4", "ipv6"],
        "remoteCidr": ["0.0.0.0/0", "::/0"],
        "remotePort": ["80", "443", "10000-20000"],
        "action": "proxy",
        "proxyServer": "main"
      }
    ]
  },
  "forwarded": {
    "fallbackAction": "pass",
    "rules": [
      { "adapterName": ["vEthernet (MyVM)"], "action": "proxy", "proxyServer": "main" }
    ]
  },
  "logLevel": "info",
  "proxyUnavailableAction": "block",
  "processingFailureAction": "block",
  "tcpFlowCapacity": 4096
}
```

- `socks5Servers`: named servers. `name` is unique (case-insensitive), `port` is 1..65535,
  `host` is an IPv4/IPv6 literal or DNS hostname. `username`/`password` are both optional or
  both present, and each UTF-8 encoding fits the RFC 1929 255-byte limit. Credentials are
  never logged; protect the configuration file's permissions.
- `host` / `forwarded`: the two policy domains. `host` is **required** and governs flows the machine
  itself originates; `forwarded` is optional and governs flows observed arriving on another NIC.
  Each domain owns its own ordered `rules` list and its own `fallbackAction`. A rule belongs to the
  domain whose list it is written in — never to both — so a rule that must apply on both sides is
  written twice, and an adapter selector narrows a rule inside its domain instead of deciding which
  domain it belongs to.
- `rules`: evaluated top to bottom within their domain; the first matching rule decides. Every match
  field is optional; fields present together use AND semantics, alternatives inside one field use OR.
  - `process`: executable filename (no slash) or path (contains `/` or `\`),
    case-insensitive. A value without a slash matches the filename; a path value matches the
    normalized full path exactly, and also matches every program in that directory or any
    subdirectory below it (e.g. `C:\Program Files\MyApp` matches `MyApp\bin\tool.exe`).
    No substring/wildcard. Rejected inside `forwarded.rules`: a forwarded flow has no host process
    owner, so such a rule could never fire.
  - `adapterId` / `adapterName`: stable id / exact friendly name. Both present = AND (must
    resolve to the same adapter). Name-only is for dynamically recreated adapters. Inside
    `forwarded.rules` they select the adapter the packet arrived on and are **optional**, so a
    forwarded rule without them applies to every forwarded flow.
  - `protocol`: `tcp` / `udp`. `addressFamily`: `ipv4` / `ipv6`.
  - `remoteCidr`: CIDR prefixes. `remotePort`: decimal ports or inclusive ranges (`10000-20000`).
  - `action`: `proxy` (requires `proxyServer`), `pass`, or `block`.
  - Present match arrays must be non-empty; an omitted field imposes no condition.
- `host.fallbackAction`: `pass` or `block`, **required**. It decides a host flow that no host rule
  matched. A host fallback proxy requires an explicit catch-all `proxy` rule.
- `forwarded.fallbackAction`: `pass` or `block`, optional, defaults to `pass`. It decides a forwarded
  flow that no forwarded rule matched. `block` drops every unmatched forwarded flow, which for a
  guest VM means losing connectivity — set it deliberately.
- `proxyUnavailableAction` / `processingFailureAction`: optional; both default to `block` and
  only `block` is accepted in the first release.
- `tcpFlowCapacity`: optional concurrent proxied TCP flow budget, `1..8192`, default `4096`.
  Each proxied TCP flow consumes two local ephemeral ports (redirect listener + SOCKS5 control),
  so the budget keeps WinForward's consumption at a safe fraction of the default Windows dynamic
  port range (~16K) including TIME_WAIT churn. New flows above the budget are blocked
  fail-closed with a `reason=capacity` trace event and a periodic info summary. Values above
  `4096` are accepted with a validation warning about port-pool pressure. Omitting the field
  deliberately tightens the limit from the previous implicit 16,384 sessions; configure a higher
  value explicitly (max 8192) if more concurrent flows are required.
- `udpSessionCapacity`: optional concurrent proxied UDP session budget, `1..16384`, default
  `16384`. Like a proxied TCP flow, each UDP session consumes two local ports (SOCKS5 control
  connection + relay socket), so values above `4096` are accepted with a validation warning that
  also names the aggregate kernel receive buffer at that capacity. A datagram for a flow above the
  budget is refused fail-closed with a rate-limited `udp.session.capacity-block` warn and the
  `udpCapacityRejections` counter; the flow retries on its next datagram.
- `udpRelayReceiveBufferKb`: optional per-relay-socket kernel receive buffer in KiB, `16..1024`,
  default `128`. It is applied to every relay socket before bind; responses can burst faster than
  one receive loop reinjects them, so the buffer absorbs the burst. Because it is per session, a
  per-session value above `256` combined with a session capacity above `2048` is accepted with a
  validation warning about the aggregate kernel receive buffer.
- `udpSessionIdleSeconds`: optional idle retention for a UDP session, `5..600`, default `30`
  seconds — after the last send or receive, the session's relay socket and its association lease
  are released (a shared association itself stays warm for reuse for another 60 s). The sweeper's
  UDP cadence derives from this value: half the idle timeout, at least 5 s, and never longer than
  the 60 s main sweep interval (15 s at the default).
- `udpAssociationReuse`: how UDP flows share authenticated SOCKS5 associations — `auto` (default),
  `always`, or `off` (case-insensitive; any other value is rejected). `auto` shares and passively
  falls back to per-flow associations for a server observed to pin one client source port per
  association — sticky for the run and reported once with a warn and the `udpAssociationFallbacks`
  counter; `always` shares with detection disabled; `off` reproduces the per-flow association
  behaviour exactly and is the rollback lever.
- `udpAssociationMaxPerServer`: per-server **ceiling** on shared associations, `1..16384`, default
  `1024`. It is a bound on connections, not a preallocation: the pool opens only the associations
  placement needs, and a flow arriving once the ceiling is reached is served from a private
  per-flow association instead of being refused.
- `udpAssociationFlowsPerAssociation`: concurrent flows one shared association serves, `1..256`,
  default `16`. It is simultaneously the blast radius of one association death (every attached flow
  fails on its next datagram and re-establishes) and the capability sampler's live-evidence set, so
  raising it trades sharing for recovery fan-out. At the defaults both caps multiply into a shared
  head of 16,384 flows per server — the default `udpSessionCapacity` — so every admitted flow can be
  shared. If the two caps multiply to less than `udpSessionCapacity`, validation warns (it does not
  refuse): the flows beyond that head are served from private per-flow associations, so they each
  hold their own control connection instead of sharing one, and the excess is invisible in every
  counter except the descriptor and port counts.
- `logLevel`: optional runtime verbosity: `error`, `warn`, `info`, `debug`, or `trace`. Values are
  case-insensitive and surrounding whitespace is ignored; omitted `logLevel` defaults to `info`.
  `info` retains concise lifecycle output, `debug` adds flow and proxy lifecycle events, and `trace`
  adds per-packet classification, policy, proxy, reinjection, drop, and terminal events. Events are
  single-line records written to stderr, each prefixed with the local wall-clock timestamp
  (`yyyy-MM-dd HH:mm:ss.fff`) so field logs can be correlated with external events, such as
  `2026-08-27 14:03:21.517 [trace] packet.completed packet=42 flow=7 disposition=pass`. Trace can
  be high volume and is intended for temporary diagnosis. Diagnostic records contain metadata and
  byte counts only: credentials, authentication traffic, payloads, and raw packet bytes are never
  logged. Process names are included when attribution succeeds; full process paths are included
  only when a rule uses a path-based process selector. A host flow's `flow.created` line is written
  when its deferred attribution claim completes, so it can follow the proxy legs that flow produced.

**UDP resource shape.** Association sharing removes the per-flow control connection, not the relay
socket: every live flow keeps its own local relay socket, because that local port plus the relay
endpoint is what routes a response back to its flow. The descriptor floor is therefore ≈1 per live
session plus one shared control connection per association (≈1/16 at the defaults), and the kernel
receive-buffer estimate stays `live sessions × udpRelayReceiveBufferKb`. The recorded
`udp.sessionBudget` soak measured 1.94–1.95 descriptors per live session at 100 new flows/s while
the pooled head was saturated at 256 flows per server (the pool's original 16 × 16 default) and
1.06 in the pooled regime; the two association caps above raise the default head to the default
session capacity, so the pooled shape covers the admitted population. Re-measured on those shipped
caps, the same 100 new flows/s load holds ≈1.06 descriptors per live session and ≈0.063 associations
per session (≈282 shared control connections for ≈4,500 live flows), with the soak's retention and
pooling verdict halves both recorded as held.

**Reply ownership.** Sharing an association also shares its reply path: the server answers on the last
peer address it saw, so a reply can arrive on a flow other than the one that asked. WinForward counts
every relay response whose declared source is not the receiving flow's own destination
(`udpResponseSourceMismatch`; the rate-limited `udp.response.foreign_source` warn carries both addresses,
the origin kind, and the association generation) and still delivers it, because RFC 1928 does not pin the
reply source and some protocols legitimately continue from a new endpoint. The comparison is by
destination address (the IPv6 scope is ignored: it belongs to the receiving interface, not to the peer),
so it observes **cross-destination** misdelivery only: two flows to the same destination are
indistinguishable by address, and their misdelivered replies are invisible to it. **A zero count
therefore does not prove that association sharing is safe for your traffic.**

The configuration is validated fully before interception starts and is kept immutable for the
lifetime of a run. Configuration hot reload is not supported.

### Runtime diagnostic events

Beyond the lifecycle messages, the runtime emits structured events (single-line
`key=value` records) that make silent failure paths and adapter-view recovery observable.
The operationally relevant ones:

| Event | Level | Meaning |
| --- | --- | --- |
| `runner.heartbeat` | info | Periodic summary (60 s): uptime, active flows, TCP/UDP sessions against their capacities, running/degraded adapter pumps, interception-health state, and key counter deltas since the previous heartbeat. |
| `flow.capacity-block` | warn | A new flow was blocked fail-closed because the flow table is at capacity (rate-limited). |
| `flow.attribution-miss` | warn | Process attribution found no owner for a host flow, so process rules did not match (rate-limited). |
| `reinject.pass-failed` | warn | The native reinjection of a pass-through frame failed (rate-limited). |
| `tcp.redirect.relaySetupFailed` | warn | The SOCKS5 relay setup for an already-redirected TCP flow failed; the client receives a RST. Carries the error kind, socket error, upstream endpoint, and attempt count. |
| `tcp.redirect.unrelatedPeer` | warn | The redirect listener accepted a peer that does not match the expected client tuple. |
| `udp.reinject.unresolved` | warn | A host UDP response could not resolve its origin adapter and fell back to the host target (rate-limited, counted). |
| `udp.reinject.drop` | warn | A UDP response was dropped fail-closed (unresolvable origin or missing client MAC; rate-limited, counted). |
| `udp.response.foreign_source` | warn | A relay response declared a source other than the receiving flow's own destination — a reply the server delivered to a different flow. It is still injected with that declared source (a mismatch is not proof of an invalid reply); counted per reply as `udpResponseSourceMismatch`, rate-limited to one line per 5 s window **per session**. A sustained flood of these lines means the server is delivering another flow's replies to the named flows of one association at a steady rate; the counter, not the line count, is the measurement, and the `udpAssociation` field groups the affected flows. |
| `udp.targets.noMac` | warn | The capture scope contains adapters without a usable MAC (forwarded responses to them drop fail-closed); emitted on first occurrence and when the affected adapter set changes, not on every adapter refresh. |
| `udp.session.capacity-block` | warn | A UDP datagram was refused fail-closed because the session budget is full and its flow has no slot (rate-limited, counted as `udpCapacityRejections`); the flow retries on its next datagram. |
| `udp.association.fallback` | warn | Passive sampling detected a SOCKS5 server that pins one client source port per association, so that server is served by per-flow associations for the rest of the run. Emitted once per server per run (counted as `udpAssociationFallbacks`). |
| `udp.association.lost` | warn | A shared SOCKS5 UDP association died without an in-place recovery; its attached flows re-establish on their next datagram (rate-limited; the association-lost removals are counted per affected flow as `udpAssociationLost`). |
| `adapter.addressQuery.failed` | debug | The periodic adapter address-fingerprint query failed (non-fatal; the refresh diff continues without addresses). |
| `runner.forcedRefresh` | warn | Interception-path failure rates crossed their thresholds, so a forced adapter-view refresh (generation rebuild) was armed. |
| `runner.forcedRefresh.degraded` | error | Forced refreshes keep triggering without a successful refresh in between; the trigger cadence drops to one per 5 minutes. |

Sustained `reinject.pass-failed`/`udp.reinject.*` clusters followed by `runner.forcedRefresh`
indicate the adapter view went stale outside WinForward's visibility (for example IPv6
temporary-address rotation); the forced refresh rebuilds the view on fresh handles.

### No implicit rules

WinForward adds **no** user-visible policy rules for its own process, DNS, loopback, or any
other traffic. Configure pass/proxy/block rules for WinForward's own traffic, DNS traffic,
and everything else you care about explicitly. The only built-in safeguard is the internal
loop-prevention registry for WinForward's own SOCKS5 control and relay sockets — it is not a
user-visible rule and does not exempt other applications using the same endpoint.

### Pass / forwarding / NAT responsibility

A `pass` decision reinjects the frame into the normal Windows networking path. WinForward does
not select routes, enable Windows IP forwarding, or create NAT rules. For forwarded Hyper-V
VM traffic, the operator must already have the required Windows forwarding/routing/NAT
configured. WinForward may detect and diagnose missing prerequisites but never changes them.

## Supported traffic

- TCP and UDP over IPv4 and IPv6.
- Proxy flows use SOCKS5 `CONNECT` (TCP) and `UDP ASSOCIATE` (UDP), with NO-AUTH or
  username/password (RFC 1929).
- UDP setup sends an all-zero `UDP ASSOCIATE` endpoint in the TCP control connection's address
  family, then binds the relay socket in the returned relay address family. The SOCKS5 UDP
  destination `ATYP` and address remain those of the original datagram, so an IPv6 destination can
  travel through an IPv4 relay when the SOCKS5 server supports it.
- Ordinary, safely parseable, unfragmented flows observed after WinForward starts.

Proxy-selected traffic is **blocked** (never silently passed) when it is malformed,
fragmented, uses TCP Fast Open data, has an unsafe/unsupported IPv6 extension-header chain,
uses SOCKS5 UDP fragmentation (`FRAG != 0`), or exceeds the pinned NDISAPI frame ABI. A
`pass` decision can reinject otherwise valid traffic without proxy-only parsing restrictions.

## Examples

See `examples/`:

- `process-proxy.json` — proxy specific processes (optionally by port/CIDR).
- `hyperv-adapter-proxy.json` — proxy flows arriving from a Hyper-V virtual adapter (`forwarded`).
- `dns-policy.json` — explicit DNS policy (proxy UDP/53, block TCP/53).
- `dns-proxy.json` — proxy all UDP/53.
- `pass-fallback.json` — proxy a process, pass everything else.
- `block-fallback.json` — proxy a process, block everything else (leak prevention).

## Notes

- Host-originated flows are governed by `host.rules` and selected by owning process, by adapter, or
  by any combination of the match fields. Forwarded flows (e.g. from a Hyper-V guest) are governed
  by `forwarded.rules` and selected by the adapter they arrived on. A forwarded flow has no host
  process owner, so `process` is rejected inside `forwarded.rules`; a host rule never applies to
  forwarded traffic and vice versa.
- `Forwarded` is currently derived from NDIS receive direction. This includes both traffic Windows
  may route across adapters and new inbound traffic addressed to a service on this host; both are
  governed by `forwarded.rules` and `forwarded.fallbackAction`.
- A trace event's `rule` field is the matched rule's index **within its domain**, so it is meaningful
  only next to the same event's `origin` field: `rule=0 origin=Forwarded` is
  `forwarded.rules[0]`.
- When a proxied TCP connection ends, WinForward keeps a short TIME_WAIT-like grace entry for the
  flow's original tuple: stragglers of the finished handshake (the final ACK, a retransmitted
  FIN/ACK) are silently dropped instead of being forwarded toward the real server, which never saw
  the proxied connection and would answer the unknown tuple with a stray RST.
- The first release is a foreground console process; native Windows Service installation is
  deferred.
