# WinForward

Windows-only, CLI-only transparent network proxy. WinForward intercepts TCP/UDP traffic
through the Windows Packet Filter driver (WinpkFilter/NDISAPI), evaluates ordered rules,
and routes matching flows through configured SOCKS5 servers — a ProxiFyre/socksify-style
transparent proxy for applications that cannot proxy themselves and for traffic arriving
through a selected network adapter (for example a Hyper-V virtual adapter).

## Requirements

- Windows 10 22H2, Windows 11, or Windows Server 2022 or later, on **x64 or ARM64**. The driver is
  kernel-mode, so the architecture has to match the operating system: ARM64 Windows needs the ARM64
  package and cannot use the x64 one, because kernel drivers are not emulated.
- The WinpkFilter driver (`ndisrd.sys`) must be installed and running.
- A matching `ndisapi.dll` sidecar next to the executable, taken from the Windows Packet Filter
  package for the same architecture (`Windows.Packet.Filter.3.6.2.1.{x64,ARM64}.msi`, which also
  installs the kernel driver). The pinned release is
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

The `dotnet publish` command above produces the AOT artifact; add `-p:PublishAot=false
--self-contained false` for the framework-dependent one. CI publishes both (see
`.github/workflows/release-build.yml`), and both are a single `WinForward.exe` to drop next to the
same matching `ndisapi.dll` (x64) sidecar:

| Artifact | What it is | Size | .NET 10 runtime | Pick it for |
| --- | --- | --- | --- | --- |
| `WinForward-win-x64` | A single native AOT executable, with no managed runtime dependency. | ≈5.2 MB | Not required. | The lowest startup latency and idle memory (≈11 ms and ≈7 MiB, against ≈184 ms and ≈34 MiB), and hosts where the runtime will not be installed. |
| `WinForward-win-x64-fdd` | The same program as a framework-dependent executable, run by the shared .NET 10 runtime instead of a native image. | ≈1.73 MiB | **Required, and it must be installed before the first run.** | The JIT's throughput — runtime instruction-set dispatch and dynamic PGO, worth 1.2–2.6x on the dispatch, flow-table and frame-rewrite paths — in the smaller download. |
| `WinForward-win-arm64`, `WinForward-win-arm64-fdd` | The same two artifacts for ARM64 Windows. | as above | as above | ARM64 Windows, with the ARM64 driver package. The x64 artifacts cannot serve it: the kernel driver is not emulated. |

The framework-dependent artifact is not self-contained: on a machine without the .NET 10 runtime
installed it fails at process start with the host's own error rather than a WinForward diagnostic,
so install the runtime first. WinForward loads the DLL from its own application directory only
(not an uncontrolled PATH/current-directory search) and diagnoses missing DLL, missing export,
wrong architecture, inaccessible driver, and incompatible version separately at startup.

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
  "localTargets": [
    { "name": "dns-in", "host": "127.0.0.1", "port": 53 }
  ],
  "host": {
    "fallbackAction": "pass",
    "rules": [
      { "protocol": ["udp"], "remotePort": ["53"], "action": "proxy", "target": "dns-in" },
      {
        "process": ["browser.exe"],
        "protocol": ["tcp", "udp"],
        "addressFamily": ["ipv4", "ipv6"],
        "remoteCidr": ["0.0.0.0/0", "::/0"],
        "remotePort": ["80", "443", "10000-20000"],
        "action": "proxy",
        "target": "main"
      }
    ]
  },
  "forwarded": {
    "fallbackAction": "pass",
    "rules": [
      { "adapterName": ["vEthernet (MyVM)"], "action": "proxy", "target": "main" }
    ]
  },
  "logLevel": "info",
  "logFormat": "auto",
  "proxyUnavailableAction": "block",
  "processingFailureAction": "block",
  "tcpFlowCapacity": 4096
}
```

- `socks5Servers`: named servers. `name` is unique (case-insensitive), `port` is 1..65535,
  `host` is an IPv4/IPv6 literal or DNS hostname. `username`/`password` are both optional or
  both present, and each UTF-8 encoding fits the RFC 1929 255-byte limit. Credentials are
  never logged; protect the configuration file's permissions. `udpOverTcp` is optional and
  defaults to `false`; it selects the UDP-over-TCP (UoT v2, connect mode) carriage for this
  server's UDP flows instead of the native per-flow `UDP ASSOCIATE` relay — see **UDP over TCP**
  below for what it does and what it costs.
- `localTargets`: optional named local UDP endpoints. `name` is unique across `socks5Servers` and
  `localTargets` (case-insensitive, one namespace), `host` must be an IP literal — a hostname would
  itself need the DNS path it is meant to configure — and `port` is 1..65535. A local target rents
  no SOCKS5 association and shares no socket: each selected flow gets its own socket, and its
  payload is forwarded to the endpoint verbatim. The flow's original destination is never sent to
  the endpoint (the local hop carries no field for it), so the endpoint answers by its own policy;
  the destination is restored only as the source address and port of the reply the client sees. A
  `host` that is not a loopback address is accepted with a validation warning naming the address
  and both consequences: the payload leaves this host in the clear, and the endpoint's replies are
  attributed to the flow's original destination.
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
  - `action`: `proxy` (requires `target`), `pass`, or `block`. `target` names a configured
    `socks5Servers` entry or a `localTargets` entry. A rule that names a local target must select
    exactly `udp`: a rule without a `protocol` selector matches every protocol, TCP included, so it
    is rejected.
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
  `16384`. Like a proxied TCP flow, each UDP session consumes two local ports on the native relay
  path (SOCKS5 control connection + relay socket) and one on a `udpOverTcp` server (the flow's
  stream connection), so values above `4096` are accepted with a validation warning that
  also names the aggregate kernel receive buffer at that capacity. A datagram for a flow above the
  budget is refused fail-closed with a rate-limited `udp.session.capacity-block` warn and the
  `udpCapacityRejections` counter; the flow retries on its next datagram.
- `udpRelayReceiveBufferKb`: optional per-relay-socket kernel receive buffer in KiB, `16..1024`,
  default `64`. It is applied to every relay socket before bind; responses can burst faster than
  one receive loop reinjects them, so the buffer absorbs the burst. Because it is per session, a
  per-session value above `256` combined with a session capacity above `2048` is accepted with a
  validation warning about the aggregate kernel receive buffer.
- `udpSessionIdleSeconds`: optional idle retention for a UDP session, `5..600`, default `30`
  seconds — after the last send or receive, the session's relay socket and its own association (its
  control connection) are released with it; nothing is kept warm for a later flow. The sweeper's
  UDP cadence derives from this value: half the idle timeout, at least 5 s, and never longer than
  the 60 s main sweep interval (15 s at the default).
- `logLevel`: optional runtime verbosity: `error`, `warn`, `info`, `debug`, or `trace`. Values are
  case-insensitive and surrounding whitespace is ignored; omitted `logLevel` defaults to `info`.
  `info` retains concise lifecycle output, `debug` adds flow and proxy lifecycle events, and `trace`
  adds per-packet classification, policy, proxy, reinjection, drop, and terminal events. Trace can
  be high volume and is intended for temporary diagnosis.
- `logFormat`: optional console record shape: `auto`, `simple`, or `json`. Values are
  case-insensitive and surrounding whitespace is ignored; omitted `logFormat` defaults to `auto`.
  `auto` selects `simple` when stderr is an interactive terminal and `json` when it is redirected,
  so a watched console stays readable while a captured log keeps every structured field. `simple`
  and `json` force that shape. Runtime logging always goes to **stderr**; stdout carries the
  `adapters` table and the `validate` confirmation.

  Both formats come from `Microsoft.Extensions.Logging.Console` and every record carries a local
  wall-clock timestamp with its UTC offset (`+08:00 yyyy-MM-dd HH:mm:ss.fff`), so a log line can be
  correlated with an external capture. `simple` renders one record as a level abbreviation, the
  category, the numeric event id, and the message, with an exception's stack trace on the following
  lines:

  ```
  +08:00 2026-08-27 14:03:21.517 dbug: WinForward.Runtime.UdpProxy.UdpProxyCoordinator[1518028212]
        UDP session created for 10.0.0.5:5353 -> 8.8.8.8:53 via main (socks5/uot), association 7.
  ```

  `json` renders one JSON object per line and keeps the message template's fields under `State`,
  which is the shape to use when a log is captured for later analysis:

  ```json
  {"Timestamp":"+08:00 2026-08-27 14:03:21.517","EventId":1518028212,"LogLevel":"Debug","Category":"udp","Message":"UDP session created for 10.0.0.5:5353 -> 8.8.8.8:53 via main (socks5/uot), association 7.","State":{"source":"10.0.0.5:5353","destination":"8.8.8.8:53","target":"main","udpTransport":"uot","udpAssociation":7}}
  ```

  Diagnostic records contain metadata and byte counts only: credentials, authentication traffic,
  payloads, and raw packet bytes are never logged. Process names are included when attribution
  succeeds; full process paths are included only when a rule uses a path-based process selector. A
  host flow's `flow.created` record is written when its deferred attribution claim completes, so it
  can follow the proxy legs that flow produced.

**UDP resource shape.** Every live UDP flow owns its own authenticated association: the SOCKS5
control connection that carried its `UDP ASSOCIATE`, and the relay socket that association
negotiated. Both are local descriptors, so the floor is two ports per live native session, and
nothing about the association is pooled, leased, or reused by another flow. The kernel receive-buffer
estimate is unchanged — `live sessions × udpRelayReceiveBufferKb`, one buffer per relay socket,
one relay socket per flow — and `udpSessionCapacity` therefore bounds live flows at two descriptors
each: validation warns above `4096` sessions, and separately when a per-session buffer above
`256` KiB meets more than `2048` sessions (see `udpRelayReceiveBufferKb` above). A `udpOverTcp`
flow keeps the same one-flow-per-owner shape with **one** descriptor: its stream connection replaces
both the control connection and the relay socket, and `udpRelayReceiveBufferKb` does not apply to it
(there is no relay socket; the stream socket keeps the operating system's own buffers).

**Reply ownership.** A reply's declared source need not be the flow's destination: RFC 1928 does not
pin the reply source, and a multi-homed or anycast server may legitimately answer from another
endpoint. WinForward counts every relay response whose declared source is not the receiving flow's
own destination (`udpResponseSourceMismatch`; the rate-limited `udp.response.foreign_source` warn
carries both addresses, the origin kind, and the association generation) and still delivers it, with
that declared source. The comparison is by destination address (the IPv6 scope is ignored: it belongs
to the receiving interface, not to the peer), so a link-local peer whose scope differs is not counted
and a genuine foreign endpoint still is.

**The guarantee: one flow per association, so the reply-ownership ambiguity a shared association has
cannot occur.** A flow's relay socket belongs to that flow alone — created, bound, and disposed with
it — so a reply can only arrive on the socket that sent the request. The observation above is
therefore about a server answering from an unexpected endpoint, not about a reply reaching the wrong
flow. **A `udpOverTcp` flow extends the same guarantee to one flow per connection**: its stream
connection is dialed, owned, and disposed with the flow, and is never shared or pooled. UoT connect
mode carries no reply source on the wire at all, so the transport declares the flow's own destination
as every reply's source; a reply from a foreign endpoint cannot even be expressed on that path, and
the counter above cannot fire there by structure rather than by suppression.

**UDP over TCP — the opt-in per-flow carriage (`udpOverTcp`).** A `socks5Servers` entry may set
`"udpOverTcp": true` (default off) to serve its UDP flows over UoT v2 in connect mode instead of the
native per-flow `UDP ASSOCIATE` relay. The flow then owns one authenticated TCP connection: the
client `CONNECT`s to the magic address `sp.v2.udp-over-tcp.arpa`, sends the UoT request header
naming the flow's single destination, and carries every datagram as a `u16be length | payload` frame
on that stream. Establishment is pipelined: the greeting (and the RFC 1929 credential message when
credentials are configured) is written without waiting for a reply, and the flow's first datagram is
written in the same flight as the `CONNECT` request and the UoT header — so it no longer waits out
the `UDP ASSOCIATE` round trip (and the relay endpoint it returns) before it can leave. The mode's
measured effect against the native per-flow column, including its first-response distribution and
its descriptor count, is in `benchmarks/results/2026-10-06-uot-per-flow/`.

**The mode's honest caveat: the datagrams now ride TCP.** Everything a stream does to carriage
applies to the flow's own datagrams. A lost segment delays every later datagram of that flow, so a
flow's own loss is its own head-of-line cost (never a sibling flow's — the connection is not shared),
and a QUIC flow carried inside stacks its congestion control on TCP's instead of running end to end.
The native per-flow relay therefore stays the recommendation on lossy or high-RTT legs. The mode is
aimed at short, non-DNS request/response flows against a nearby or trusted proxy — the population
whose cost is the setup round trip rather than the carriage — and it is a per-server opt-in: a server
that does not set `udpOverTcp` is served exactly as before.

**Local targets — the recommended placement for DNS-shaped flows.** A flow selected onto a local
target gets its own socket and no SOCKS5 association: only that flow's replies arrive on it, so the
reply-ownership question above cannot arise at that hop. The
payload is forwarded verbatim and the transport declares the flow's original destination as the
reply's source, so the client sees the answer from the address and port it sent to; the session's
source check, the foreign-source counter, and the reinjector are unchanged, and a datagram from
anything but the configured endpoint never reaches them (it is skipped at the transport, like the
relay's other skip classes). `udpLocalTargetFlows` counts flows created over a local target,
`udpLocalTargetFailures` counts a send the transport could not hand to its socket or a fatal receive
fault (never the cancellation or disposal that ends a session), and such a flow fails closed and
re-establishes on its next datagram. The `udp.session.created`
debug event carries `target=<name>`, `targetKind=local|socks5`, and `udpTransport=uot|native`
(the third field is present for a SOCKS5 target and null for a local target or for the teardown
events, which carry no target).
`benchmarks/results/2026-10-05-no-association-sharing/` measures the shipped series: both surviving
columns answer 48 of 48 flows per churn wave and 48 of 48 in the burst, while the local column pays
**zero SOCKS5 handshakes**, ≈7.0–8.1 KB per session against the per-flow column's ≈84.5–93.7 KB, and
a first-response p50 of 3.0–4.5 ms against 155.7–164.1 ms. (The per-flow figure is the harness's
serialized-setup shape — a 50 ms dial delay through the 8-wide setup limiter — not a per-flow network
cost.)

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
| `udp.response.foreign_source` | warn | A relay response declared a source other than the receiving flow's own destination. It is still injected with that declared source — RFC 1928 does not pin the reply source, and a server may legitimately answer from another endpoint — and counted per reply as `udpResponseSourceMismatch`, rate-limited to one line per 5 s window **per session**. The line carries both addresses, the origin kind, and the flow's `udpAssociation` correlation id; the counter, not the line count, is the measurement. A sustained rate means the server is answering these flows from an unexpected endpoint, not that a reply reached the wrong flow. |
| `udp.targets.noMac` | warn | The capture scope contains adapters without a usable MAC (forwarded responses to them drop fail-closed); emitted on first occurrence and when the affected adapter set changes, not on every adapter refresh. |
| `udp.session.capacity-block` | warn | A UDP datagram was refused fail-closed because the session budget is full and its flow has no slot (rate-limited, counted as `udpCapacityRejections`); the flow retries on its next datagram. |
| `udp.association.lost` | warn | A flow's own SOCKS5 UDP association died — its control connection ended or faulted, and there is no in-place recovery — so that flow is failed closed and re-establishes on its next datagram with a fresh association (rate-limited across the composition; the association-lost removals are counted per affected flow as `udpAssociationLost`). |
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
  username/password (RFC 1929). A `proxy` rule may instead select a `localTargets` entry for UDP
  flows, which reach that endpoint on a per-flow socket with no SOCKS5 control connection; TCP always
  uses a `socks5Servers` entry. A UDP flow through a `socks5Servers` entry with `udpOverTcp` uses a
  SOCKS5 `CONNECT` to the UoT magic address instead of `UDP ASSOCIATE`, and carries its datagrams as
  length-prefixed frames on that connection.
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
- `local-dns.json` — answer all UDP/53 from a local target instead of a SOCKS5 relay.
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
