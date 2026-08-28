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
  "rules": [
    {
      "process": ["browser.exe"],
      "protocol": ["tcp", "udp"],
      "addressFamily": ["ipv4", "ipv6"],
      "remoteCidr": ["0.0.0.0/0", "::/0"],
      "remotePort": ["80", "443", "10000-20000"],
      "action": "proxy",
      "proxyServer": "main"
    },
    { "adapterName": ["vEthernet (MyVM)"], "action": "pass" }
  ],
  "logLevel": "info",
  "fallbackAction": "pass",
  "proxyUnavailableAction": "block",
  "processingFailureAction": "block",
  "tcpFlowCapacity": 4096
}
```

- `socks5Servers`: named servers. `name` is unique (case-insensitive), `port` is 1..65535,
  `host` is an IPv4/IPv6 literal or DNS hostname. `username`/`password` are both optional or
  both present, and each UTF-8 encoding fits the RFC 1929 255-byte limit. Credentials are
  never logged; protect the configuration file's permissions.
- `rules`: evaluated top to bottom; the first matching rule decides. Host-originated traffic
  considers every rule. New forwarded traffic considers only rules containing `adapterId` and/or
  `adapterName`; if none match, it passes unchanged. The same split applies to packets that cannot
  be classified as TCP/UDP flows. Every match field is optional; fields present together use AND
  semantics, alternatives inside one field use OR.
  - `process`: exact executable filename (no slash) or normalized full path (contains `/` or
    `\`), case-insensitive. A value without a slash matches the filename; with a slash matches
    the full path. No substring/wildcard.
  - `adapterId` / `adapterName`: stable id / exact friendly name. Both present = AND (must
    resolve to the same adapter). Name-only is for dynamically recreated adapters.
  - `protocol`: `tcp` / `udp`. `addressFamily`: `ipv4` / `ipv6`.
  - `remoteCidr`: CIDR prefixes. `remotePort`: decimal ports or inclusive ranges (`10000-20000`).
  - `action`: `proxy` (requires `proxyServer`), `pass`, or `block`.
  - Present match arrays must be non-empty; an omitted field imposes no condition.
- `fallbackAction`: `pass` or `block` (required) for host-originated traffic. A host fallback proxy
  requires an explicit catch-all `proxy` rule. Forwarded traffic does not use this fallback.
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
  only when a rule uses a path-based process selector.

The configuration is validated fully before interception starts and is kept immutable for the
lifetime of a run. Configuration hot reload is not supported.

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
- `hyperv-adapter-proxy.json` — proxy flows arriving from a Hyper-V virtual adapter.
- `dns-policy.json` — explicit DNS policy (proxy UDP/53, block TCP/53).
- `dns-proxy.json` — proxy all UDP/53.
- `pass-fallback.json` — proxy a process, pass everything else.
- `block-fallback.json` — proxy a process, block everything else (leak prevention).

## Notes

- Transparent interception of host-originated flows is selected by owning process; forwarded
  traffic (e.g. from a Hyper-V guest) is selected by originating adapter. Forwarded traffic
  requires an adapter-qualified rule and otherwise passes, independently of unqualified rules and
  `fallbackAction`. It has no host process owner, so process rules do not match it.
- `Forwarded` is currently derived from NDIS receive direction. This includes both traffic Windows
  may route across adapters and new inbound traffic addressed to a service on this host; both use
  the adapter-qualified-only policy semantics.
- The first release is a foreground console process; native Windows Service installation is
  deferred.
