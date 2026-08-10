# WinForward Windows Proxy

## Goal

Build a Windows-only, CLI-only transparent network proxy named **WinForward**. It must intercept traffic through Windows Packet Filter (WinpkFilter/NDISAPI), evaluate ordered rules, and route matching traffic through configured SOCKS5 servers in a Proxifier-like manner.

The intended user value is to proxy applications that do not support proxies themselves and to proxy traffic arriving through a selected network adapter, such as a Hyper-V virtual adapter.

## Background and Confirmed Constraints

- Implementation language: C#.
- Target framework: .NET 10 LTS with C# 14. The original .NET 14 wording was resolved as a request for the C# 14 language version.
- Deployment model: Native AOT executable.
- First-release supported operating systems and architecture: Windows 10 22H2 x64, Windows 11 x64, and Windows Server 2022 or later x64.
- The first release does not support x86, ARM64, earlier Windows 10 versions, or Windows Server 2016/2019.
- C# nullable reference types must be enabled.
- The solution shall enable the requested analyzers at the centrally managed versions: `Meziantou.Analyzer` 3.0.104, `Microsoft.VisualStudio.Threading.Analyzers` 17.14.15, `Roslynator.Analyzers` 4.15.0, and `SonarAnalyzer.CSharp` 10.27.0.140913.
- Performance is a first-class requirement for packet processing. Allocation-free and `unsafe` implementations are allowed when they preserve the documented native ABI, packet ownership, lifecycle safety, and testability.
- WinForward requires an elevated administrator token. It shall detect a non-elevated launch before opening NDISAPI or changing adapter mode, emit an actionable diagnostic, and exit without partial packet-filter state.
- Platform: Windows only.
- User interface: command line only; no GUI.
- The first release runs as a foreground console process only. Native Windows Service installation and lifecycle commands are deferred.
- Configuration format is JSON. Its top level contains `socks5Servers`, ordered `rules`, `fallbackAction`, `proxyUnavailableAction`, and `processingFailureAction`; a proxy rule references a server through `proxyServer`.
- Target machines are expected to have the Windows Packet Filter driver installed.
- Packet interception must use Windows Packet Filter via NDISAPI, not WinDivert.
- Required traffic coverage is TCP and UDP over both IPv4 and IPv6.
- Configuration contains multiple SOCKS5 servers and an ordered rule list.
- Rules are evaluated from top to bottom; the first matching rule determines the action.
- The first-release rule model is accepted as: optional `process`, optional `adapterId`, optional `adapterName`, optional `protocol`, optional `addressFamily`, optional `remoteCidr`, optional `remotePort`, required `action` (`proxy`, `pass`, or `block`), and a proxy-server reference when `action=proxy`.
- Every match field in a rule is optional; fields present together use AND semantics. For example, `process` plus `remoteCidr` requires both conditions to match.
- Each match field accepts an array of alternatives with OR semantics within that field. For example, `process: ["a", "b"]` matches either process `a` or process `b`. This applies independently to `process`, `adapterId`, `adapterName`, `protocol`, `addressFamily`, `remoteCidr`, and `remotePort`.
- `remotePort` uses a non-empty string array; each item is either a decimal port (`"53"`) or an inclusive range (`"10000-20000"`), with values in `1..65535` and the range start no greater than its end.
- All configured rule match arrays must be non-empty. Omitting a match field means that field imposes no condition; an empty array is invalid rather than a catch-all.
- When both `adapterId` and `adapterName` are supplied, they are separate fields and therefore use AND semantics. A rule may intentionally use only `adapterName` for dynamically recreated adapters.
- Rules use first-match-wins semantics.
- The fallback action is explicit and configurable; `pass` is the recommended default, while `block` is available for leak-prevention deployments.
- The first release defaults `proxyUnavailableAction` to `block` and `processingFailureAction` to `block`.
- A selected `proxy` flow shall never be silently downgraded to `pass` because its SOCKS5 server is unavailable or because proxy setup fails.
- Configuration may omit `proxyUnavailableAction` and `processingFailureAction`, in which case both default to `block`; if present, both fields must still be `block` in the first release.
- WinForward shall not embed user-visible policy rules for its own process, DNS, loopback, or any other traffic. Apart from mandatory internal loop-prevention safeguards for WinForward-owned SOCKS5 control/relay traffic, policy evaluation is mechanical and uses only the configured ordered rules and fallback action.
- WinForward shall track and exempt its own SOCKS5 control and relay sockets from recursive interception. This is an internal loop-prevention safeguard, not a user-visible policy rule and not a general exemption for other applications using the same endpoint.
- WinForward makes the policy decision before the original forwarded packet is processed by the Windows TCP/IP stack: `proxy` consumes the original flow and relays it through SOCKS5, `block` consumes and drops it, and `pass` reinjects it into the normal Windows networking path.
- `pass` does not mean that WinForward chooses a particular route, enables forwarding, or performs NAT. The Windows route table and any required forwarding/NAT configuration remain authoritative and are not modified by WinForward.
- For a `proxy` flow, the original destination flow bypasses the Windows route decision; WinForward's own SOCKS5 control and relay connections are separate host-originated traffic and use normal Windows networking to reach the configured SOCKS5 endpoint.
- Two distinct traffic-identification cases are required:
  - host-originated traffic can be selected by its owning process;
  - forwarded traffic can be selected by its originating network adapter, including Hyper-V virtual adapters.
- Confirmed Hyper-V gateway scenario: multiple VMs attach to the same Hyper-V virtual switch, use the Windows host address on `vEthernet 1` as their default gateway, and WinForward applies `pass` or `proxy` to flows arriving from that virtual adapter.
- The Hyper-V adapter scenario does not require WinForward to modify the Windows route table. For `pass`, Windows must already be configured to forward and, where necessary, NAT the VM traffic. For `proxy`, WinForward owns the proxy-flow mapping and does not require the original VM destination to be routable through the host's normal route lookup.
- WinForward shall not enable IP forwarding, modify routes, create NAT rules, or implement a general-purpose router. It may detect and diagnose missing Windows forwarding/routing/NAT prerequisites but shall not change them automatically.
- Repository inspection found no product code or established C# conventions yet. The project is greenfield.

## Requirements

### R1 — Runtime and deployment

- WinForward shall publish as a Windows Native AOT CLI executable targeting .NET 10 LTS with C# 14.
- All projects shall enable nullable reference types.
- Startup shall detect unsupported operating systems, insufficient privilege, a missing/inaccessible Windows Packet Filter driver, and an incompatible native NDISAPI library, then fail with an actionable diagnostic.

### R2 — Packet coverage

- WinForward shall handle TCP and UDP traffic for IPv4 and IPv6.
- For `proxy`, the first release supports ordinary, safely parseable, unfragmented TCP/UDP flows observed after WinForward starts. TCP uses a local transparent redirect/listener plus a separate SOCKS5 `CONNECT` relay; UDP uses a local packet redirect/relay plus SOCKS5 `UDP ASSOCIATE`.
- The first release does not proxy TCP Fast Open data, fragmented IPv4/IPv6 traffic, unsafe/unsupported IPv6 extension-header chains, SOCKS5 UDP frames with `FRAG != 0`, or frames that exceed the pinned NDISAPI packet-buffer ABI. Such traffic selected for `proxy` is blocked rather than silently passed.
- A `pass` decision can reinject otherwise valid traffic without applying proxy-only parsing restrictions.
- Intercepted packets shall be reinjected, proxied, or dropped according to the first matching rule without accidentally duplicating traffic.
- WinForward's own upstream SOCKS5 traffic shall not be recursively intercepted.

### R3 — SOCKS5 servers

- Configuration shall support multiple named SOCKS5 servers.
- Each SOCKS5 server object contains `name`, `host`, `port`, and optional paired `username`/`password` fields. Server names are non-empty and unique case-insensitively; ports are in `1..65535`; username/password are either both omitted or both present and each fits the RFC 1929 one-octet encoded-length limit.
- A proxy rule shall reference a configured server by name.
- SOCKS5 TCP `CONNECT` and UDP `ASSOCIATE` shall be supported.
- The first release shall support SOCKS5 `NO AUTH` and username/password authentication (RFC 1929).
- A configured SOCKS5 server endpoint may use an IPv4 literal, IPv6 literal, or DNS hostname.
- IPv4 and IPv6 destination addresses shall be preserved in SOCKS5 requests.
- SOCKS5 framing shall support IPv4, IPv6, and domain address encodings. Transparent intercepted flows normally expose an already-resolved destination IP rather than the original DNS name, so WinForward shall not claim to recover an original hostname from packet traffic alone.
- GSSAPI, SOCKS5-over-TLS, and other non-standard authentication or transport extensions are not required in the first release.
- SOCKS5 credentials shall never be emitted in logs or configuration-validation diagnostics. The documentation shall warn operators to protect configuration-file permissions.

### R4 — Ordered rules

- Rules shall be evaluated in configuration order.
- Evaluation shall stop at the first matching rule.
- The fallback behavior when no rule matches shall be explicit and safe.
- The MVP match fields and actions are the fields and actions listed in the confirmed constraints above.
- `fallbackAction` accepts only `pass` or `block`; a fallback proxy requires an explicit catch-all `proxy` rule so that its `proxyServer` reference is unambiguous.

### R5 — Process selection

- Rules shall be able to identify host-originated flows by process executable name and/or executable path.
- A `process` match value without a slash shall match the executable filename exactly, case-insensitively; a value containing a slash or backslash shall match the normalized full executable path exactly, case-insensitively. Substring and wildcard matching are not implied by the first release.
- Process identity lookup shall be cached without permanently associating reused PIDs with stale executable identities.
- When process identity cannot be determined, every rule containing `process` is treated as not matching. WinForward shall not guess an owner; evaluation continues with later rules and then the configured fallback action.
- Process matching cannot be assumed for packets merely forwarded from a VM or another adapter, because those packets do not belong to a host user process.
- UDP flow ownership shall not use PID or DNS transaction ID as its routing key. The canonical key shall retain address family, protocol, origin kind/adapter generation where applicable, and the original local and remote endpoints (the UDP 5-tuple plus origin context). Multiple datagrams from one socket, including concurrent DNS queries, remain on the same association when their tuple is the same; datagrams to different remote endpoints receive distinct flow mappings.
- NDISAPI/IP Helper cannot expose a socket identifier for two UDP sockets that simultaneously reuse the same local endpoint and target the same remote endpoint. WinForward shall not claim to split such packets by socket; they share one logical mapping or follow the documented ambiguous/unknown-owner fallback, and must never be assigned nondeterministically to different associations.

### R6 — Network-adapter selection

- Rules shall be able to select traffic associated with a configured Windows network adapter.
- Adapter configuration shall support both a stable adapter identifier and an exact human-readable adapter name. Name-only matching is required for dynamically created adapters whose identifier may not be stable across recreation.
- Adapter name matching shall be case-insensitive and shall not silently use substring matching.
- If an exact name-only selector resolves to multiple current adapters, startup shall fail with an ambiguity diagnostic rather than silently selecting all of them. The diagnostic shall list the conflicting identifiers and names.
- CLI adapter discovery shall show both the stable identifier and human-readable adapter name, including virtual adapters such as Hyper-V adapters.
- The design shall cover virtual adapters, including Hyper-V adapters.
- The first release does not need a user-facing ingress/egress selector. An adapter selector identifies the guest/remote-side adapter from which a new forwarded flow originates; WinForward may capture both packet directions internally to maintain and reverse the flow, but shall evaluate the ordered rules only once for the logical flow rather than independently at every observed adapter boundary.
- For forwarded traffic, the first `ON_RECEIVE` packet observed on the selected adapter establishes the logical flow's origin adapter. For host-originated traffic, the initial `ON_SEND` packet and its owning process establish the host flow. Later observations on other adapters reuse the existing flow decision and do not restart ordered-rule evaluation.
- A configured adapter selector that resolves to no current adapter shall fail startup with an actionable diagnostic. Adapter recreation after a successful startup is handled through adapter-list change processing and re-resolution; ambiguity or loss of a required selector is fail-closed rather than silently ignored.

### R7 — CLI and configuration

- The program shall load and validate a human-editable configuration file before packet interception begins.
- JSON parsing and serialization shall use a Native-AOT-compatible approach, including source-generated `System.Text.Json` metadata where required.
- Users shall explicitly configure pass/proxy/block rules for WinForward's own traffic, DNS traffic, and other traffic they want handled; the product shall provide examples/documentation but shall not silently inject such rules.
- The first release validates and loads configuration before interception and keeps an immutable configuration snapshot for the lifetime of a run; configuration hot reload is not supported.
- Configuration errors shall identify the relevant server/rule and field.
- The CLI shall provide at least a run command, a configuration validation command, and an adapter discovery command.
- The run command shall support graceful shutdown from `Ctrl+C`/console termination and release packet-filter state before exiting.
- Operational status and failures shall be emitted as structured, useful console logs without exposing SOCKS5 credentials.

### R8 — Safe lifecycle

- Normal shutdown and startup failure shall restore/pass through traffic and release Windows Packet Filter state cleanly.
- Fatal processing failures shall use an explicit, documented fail-closed behavior in the first release rather than leaving traffic in an indeterminate state.
- When a selected SOCKS5 server is unavailable or proxy setup fails, the selected flow shall be blocked rather than passed through.
- `proxyUnavailableAction` and `processingFailureAction` are optional first-release configuration fields. If omitted, each defaults to `block`; if present, each must be `block`, and other values are validation errors.

## Acceptance Criteria

- [ ] A documented Windows build publishes a Native AOT executable targeting .NET 10 LTS with C# 14, nullable analysis enabled, and no unintended managed-runtime dependency.
- [ ] A non-elevated launch fails before interception begins with an actionable administrator-required diagnostic.
- [ ] On a supported Windows test host with Windows Packet Filter installed, the CLI lists physical and Hyper-V adapters with stable identifiers.
- [ ] Configuration validation rejects duplicate server names, missing server references, invalid endpoints, unsupported rule values, and an undefined fallback policy before interception starts.
- [ ] Configuration validation rejects a missing configured adapter, an ambiguous name-only adapter selector, empty rule match arrays, and a fallback `proxy` action.
- [ ] No user-visible policy rule is implicitly added by the executable; configured rule order and fallback are the only policy inputs, with only non-overridable loop-prevention safeguards applied to WinForward-owned upstream traffic.
- [ ] An ordered-rule test proves that only the first matching rule is applied.
- [ ] When process attribution is available and unambiguous, matching host-process TCP connections work through SOCKS5 for IPv4 and IPv6 destinations; an attribution miss follows the documented unknown-owner behavior.
- [ ] When process attribution is available and unambiguous, matching host-process UDP traffic works through SOCKS5 UDP association for IPv4 and IPv6 destinations; an attribution miss follows the documented unknown-owner behavior.
- [ ] A same-process burst test with concurrent DNS-style datagrams proves that packets from one source socket are delivered through one association without response cross-wiring, while datagrams with distinct local/remote endpoint tuples resolve to distinct mappings.
- [ ] A socket-reuse/ambiguous-owner test proves that identical local-and-remote UDP tuples are not nondeterministically split across associations; the implementation records the documented shared-mapping or unknown-owner outcome.
- [ ] TCP and UDP traffic selected from a Hyper-V virtual adapter can be routed through SOCKS5 without requiring a host PID match.
- [ ] Pass-through traffic is reinjected into the normal Windows networking path without being sent to a SOCKS5 server or changing the route table.
- [ ] WinForward's SOCKS5 control and relay traffic is excluded from interception, preventing proxy loops.
- [ ] Graceful shutdown restores normal traffic flow and does not leave an adapter in tunnel mode.
- [ ] Proxy-selected unsupported or malformed traffic is blocked or rejected according to the documented fail-closed behavior and is never silently passed.
- [ ] Automated tests cover configuration parsing, rule ordering, process/path matching, adapter matching, TCP/UDP SOCKS5 framing, IPv4/IPv6 handling, loop prevention, and lifecycle cleanup where hardware-independent testing is possible.

## Out of Scope (until explicitly selected)

- GUI or tray application.
- Bundling or installing the Windows Packet Filter kernel driver.
- Non-Windows operating systems.
- HTTP/HTTPS proxy upstreams or protocols other than SOCKS5.
- GSSAPI authentication, SOCKS5-over-TLS, and other SOCKS5 transport extensions.
- Identifying the guest-OS process that originated traffic forwarded through a Hyper-V adapter without cooperation from the guest.
- General-purpose router, DHCP server, or DNS server functionality.
- TLS wrapping of SOCKS5, traffic inspection, content filtering, or packet capture UI.
- Native Windows Service installation, control, and recovery integration.

