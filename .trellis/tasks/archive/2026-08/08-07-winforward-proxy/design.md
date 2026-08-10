# WinForward Technical Design

## 1. Design Goals and Constraints

WinForward is a Windows-only Native AOT CLI that intercepts TCP/UDP packets with WinpkFilter/NDISAPI, assigns exactly one configured policy decision to each logical flow, and either passes, blocks, or bridges the flow through SOCKS5.

The design optimizes for:

- correct packet ownership before micro-optimization;
- allocation-free packet parsing and rewriting on the capture hot path;
- a narrow native C ABI seam around `ndisapi.dll`;
- immutable, precompiled policy state;
- hardware-independent tests for protocol and policy logic;
- transactional startup and shutdown that never leaves partially applied adapter modes;
- fail-closed proxy behavior without silently changing a selected flow to `pass`.

The first release targets `net10.0-windows`, C# 14, Native AOT, and `win-x64`. It requires an installed compatible WinpkFilter driver and a matching controlled `ndisapi.dll` x64 sidecar. It does not use C++/CLI, WFP, or WinDivert.

## 2. Solution and Module Boundaries

Use a small number of deep modules with one-way dependencies:

```text
WinForward.Cli
  -> WinForward.Runtime
      -> WinForward.Configuration
      -> WinForward.Core
      -> WinForward.Protocols
      -> WinForward.Windows
      -> WinForward.NdisApi

WinForward.Tests
WinForward.Windows.Tests
WinForward.IntegrationTests
```

### `WinForward.Cli`

- Implements `run`, `validate`, and `adapters` without reflection-heavy command frameworks.
- Maps typed results to stable exit codes and structured console events.
- Installs `Ctrl+C` and console termination cancellation.
- Contains no packet, SOCKS5, or NDISAPI logic.

### `WinForward.Configuration`

- Owns raw JSON DTOs and source-generated `System.Text.Json` metadata.
- Converts raw DTOs into an immutable normalized snapshot.
- Produces diagnostics with JSON field path, rule/server index, and redacted values.
- Resolves server references and precompiles process selectors, CIDRs, port ranges, protocols, and address families.

### `WinForward.Core`

- Owns domain values: endpoints, flow keys, adapter identity, process identity, policy decisions, rule indexes, actions, and failure classifications.
- Implements pure ordered-rule evaluation.
- Defines the flow state and exactly-once packet-disposition contracts.
- Has no Windows or native dependency.

### `WinForward.Protocols`

- Parses Ethernet II, bounded IPv4/IPv6, TCP, and UDP from spans.
- Rewrites packet endpoints and recalculates IPv4/TCP/UDP checksums.
- Implements SOCKS5 greeting, RFC 1929, `CONNECT`, `UDP ASSOCIATE`, and SOCKS5 UDP framing.
- Returns typed parse/transform results; it does not decide policy or perform socket I/O.

### `WinForward.Windows`

- Checks supported OS, x64 architecture, and elevated administrator token.
- Correlates NDISAPI adapter enumeration with IP Helper adapter identity.
- Implements best-effort TCP/UDP process attribution through IP Helper tables.
- Owns Windows socket and console-host helpers that are not NDISAPI-specific.

### `WinForward.NdisApi`

- Isolates all source-generated P/Invoke declarations and ABI structs.
- Owns the filter-driver `SafeHandle`, adapter runtime handles, events, packet batches, unmanaged buffer pools, and send/read calls.
- Presents packet leases and typed dispositions to the runtime rather than leaking native pointers.

### `WinForward.Runtime`

- Coordinates validation, adapter resolution, safety registry, local relays, adapter modes, capture pumps, flow table, shutdown, and fatal-failure handling.
- Applies pass/proxy/block dispositions exactly once.
- Owns bounded queues, timeouts, and concurrency.

## 3. Configuration Contract

The JSON shape is:

```json
{
  "socks5Servers": [
    {
      "name": "main",
      "host": "proxy.example.com",
      "port": 1080,
      "username": "user",
      "password": "secret"
    }
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
    {
      "adapterName": ["vEthernet 1"],
      "action": "pass"
    }
  ],
  "fallbackAction": "pass",
  "proxyUnavailableAction": "block",
  "processingFailureAction": "block"
}
```

JSON uses camel-case property and enum tokens and rejects unknown properties. Present match arrays must be non-empty. Rules preserve input order and retain their zero-based index for diagnostics and structured logs.

Validation is complete before any native driver operation:

- server names are non-empty and unique with ordinal case-insensitive comparison;
- `host` is an IPv4 literal, IPv6 literal, or valid DNS hostname; `port` is `1..65535`;
- username and password are both omitted or both present, and their UTF-8 encodings fit RFC 1929's 255-byte fields;
- `proxy` requires a valid `proxyServer`; `pass` and `block` reject `proxyServer`;
- `fallbackAction` is required and accepts only `pass`/`block`;
- both first-release failure-action fields must be present or default to `block`, and only `block` is accepted;
- process selectors, CIDRs, ports/ranges, protocols, and address families are parsed at load time;
- an ID/name adapter selector must resolve to the same current adapter; name-only ambiguity and missing configured adapters are errors.

Raw DTOs never reach the packet path. Conversion produces a frozen snapshot with parsed address prefixes, normalized exact process strings, sorted/merged port intervals, server lookup, and resolved adapter identities. Configuration hot reload is absent.

## 4. NDISAPI Native Seam and ABI

Pin one upstream NDISAPI header/DLL/driver-compatible release. The implementation uses the native exported C ABI through `[LibraryImport]`, explicit entry points, and `CallConvStdcall`. Shared structures use pointer-sized handles, explicit unions/fixed buffers, and `Pack = 1` exactly as the pinned header requires.

The first implementation gate is a small native x64 ABI probe compiled from that exact header. It emits `sizeof` and `offsetof` for every used structure/field, including:

- `TCP_AdapterList`;
- `INTERMEDIATE_BUFFER`;
- `ETH_REQUEST` / batch request structures;
- adapter mode and event request structures;
- packet-buffer capacity (`MAX_ETHER_FRAME` versus a jumbo-enabled ABI).

Managed ABI tests compare those values. A mismatch blocks release. The native DLL is loaded from a controlled application-relative path, not an uncontrolled current directory or arbitrary `PATH`; startup distinguishes missing DLL, missing export, wrong architecture, inaccessible driver, and incompatible version.

### Packet Ownership

Every captured packet lease reaches one terminal state:

```text
Pass          -> reinjected once in the captured direction
Block         -> consumed/released once
ProxyConsumed -> transformed/handed to a proxy flow; original released once
```

For normal pass-through:

| Captured direction | Reinjection |
|---|---|
| `PACKET_FLAG_ON_SEND` | send to adapter |
| `PACKET_FLAG_ON_RECEIVE` | send to MSTCP |

Native read slots cannot outlive their batch unless an explicit lease retains them. Cross-worker handoff either owns a dedicated unmanaged frame buffer or copies into a bounded pool. No queue may retain a pointer that the next read can overwrite. Queue capacity exhaustion follows fail-closed processing behavior.

## 5. Adapter Discovery and Origin Semantics

NDISAPI supplies runtime adapter handles/internal names; IP Helper supplies stable adapter name/GUID/LUID/friendly name. Correlation uses stable identities plus MAC/medium sanity checks, never list index. `adapters` prints at least stable ID and exact friendly name, plus useful diagnostic fields such as GUID/LUID, internal name, MAC, medium, and MTU.

Configuration uses:

- `adapterId`: the canonical stable ID printed by discovery;
- `adapterName`: exact ordinal case-insensitive friendly-name match;
- both present: AND; both must resolve to the same adapter.

For forwarded traffic, the first `ON_RECEIVE` packet on a selected guest/remote-side adapter establishes `OriginKind.Forwarded` and the origin adapter. For host traffic, the initial `ON_SEND` observation plus local socket ownership establishes `OriginKind.Host`. A flow later observed on another adapter reuses its stored decision; capture direction is not a second policy decision.

Adapter runtime handles are never persisted. Adapter-list change notification triggers correlation rebuilding. Loss or ambiguity of a configured adapter initiates fail-closed shutdown rather than silently widening/narrowing policy.

## 6. Process Attribution

NDISAPI packets do not carry PID. `WindowsProcessAttributor` samples IPv4/IPv6 TCP and UDP owner tables:

- TCP lookup uses the local/remote tuple;
- UDP lookup uses the local endpoint and wildcard-bound fallback and can remain ambiguous;
- forwarded adapter traffic has no host process owner;
- executable path comes from `QueryFullProcessImageNameW` when accessible.

Process identity cache keys include PID and process creation time. PID alone or TTL alone is unsafe because Windows reuses PIDs. Unknown or ambiguous attribution causes process-containing rules not to match; evaluation continues. A short, bounded retry for a first TCP SYN is allowed, but queues, bytes, and deadline must be bounded and timeouts fail according to policy.

## 7. Policy and Flow Ownership

Policy evaluation is a pure operation over an immutable `FlowContext`:

```text
AND between populated fields
OR between alternatives inside one field
first matching rule wins
otherwise fallbackAction
```

A decision contains the action, matched rule index (or fallback), and resolved proxy server reference where relevant. No implicit DNS, loopback, LAN, or self-process policy rule is inserted.

The flow table is checked before policy. A canonical original-flow key includes address family, protocol, the original local and remote UDP/TCP endpoints (a bidirectional tuple), origin kind, and origin adapter identity/generation where necessary. PID is attribution metadata only and is never the sole flow key. Records include:

- original endpoints and L2 metadata needed for reverse injection;
- process attribution result;
- immutable decision;
- translated/local relay tuple aliases;
- TCP redirect state or UDP association state;
- bounded pending packets/bytes;
- activity/deadline/expiry and close generation.

Concurrent observations atomically claim one flow. Retransmissions and reverse packets resolve aliases and never re-evaluate policy. Generation/expiry prevents a reused tuple or adapter handle from matching stale state. For UDP, multiple datagrams on one socket with the same local/remote tuple intentionally share an association; a datagram sent to another remote endpoint gets another key/association. UDP transaction IDs are payload data and are not used for transport routing. If two sockets reuse the same local endpoint and remote tuple, NDISAPI has no socket identifier to separate them, so the runtime must use one deterministic shared mapping (or the configured unknown-owner/fail-closed result) rather than assigning packets nondeterministically.

## 8. Action Data Paths

### `pass`

Reinject the captured frame exactly once in its normal direction. WinForward does not select routes, enable Windows forwarding, or create NAT. For forwarded VM traffic, the operator must provide any required Windows forwarding/routing/NAT.

### `block`

Consume the frame without reinjection. Initial implementation may silently drop; generated TCP resets are optional and, if added, must be marked as flow-owned internal traffic.

### TCP `proxy`

Use the proven WinpkFilter local-redirect pattern rather than attempting to send application bytes directly to a SOCKS server:

1. Claim the original flow on its initial SYN.
2. Save the original destination, source endpoint, origin adapter, and L2 context.
3. Rewrite/swap packet addresses and destination port so the Windows TCP stack delivers it to a WinForward local TCP listener; recalculate checksums and inject toward MSTCP.
4. The listener accepts the resulting local connection and resolves it through the stored translated tuple.
5. Open a separate WinForward-owned socket to the selected SOCKS5 server, negotiate authentication, and send `CONNECT` for the original IPv4/IPv6 destination.
6. Relay bytes asynchronously between the accepted local socket and the SOCKS5 socket with bounded buffers/backpressure.
7. Reverse packets on the local redirect leg are rewritten to preserve the original remote endpoint as observed by the client and injected toward the original host stack or VM adapter.

The endpoint rewrite does not insert bytes into the client's TCP sequence space; SOCKS negotiation occurs on the separate upstream connection. TCP sequence/ACK numbers on the local redirected leg remain unchanged. The implementation must still correctly preserve SYN retransmissions, TCP options, FIN/RST, half-close, and checksums. TCP Fast Open data is not supported in release 1. A Windows/Hyper-V proof-of-concept is a release gate before scaling the runtime.

### UDP `proxy`

Use a local UDP redirect/relay so normal WinForward sockets own the SOCKS transport:

1. Claim the first datagram and record the original flow/L2 context.
2. Queue it in a bounded per-flow setup queue.
3. Establish a SOCKS5 control TCP socket, negotiate authentication, bind a WinForward UDP socket, issue `UDP ASSOCIATE`, and retain the control socket.
4. Register the returned dynamic relay endpoint in loop-prevention state.
5. Encode `RSV=0`, `FRAG=0`, original destination ATYP/address/port and payload; send to the SOCKS relay.
6. Validate relay responses, remove SOCKS5 UDP framing, restore the remote-to-original-client tuple, recalculate checksums, and inject toward MSTCP or the saved VM adapter.

Release 1 uses one association per logical UDP flow for unambiguous reverse mapping. A logical UDP flow is keyed by address family, protocol, origin context, local endpoint, and remote endpoint. Associations have idle expiry, bounded queues, and a generation. SOCKS5 UDP fragmentation (`FRAG != 0`) and IP fragmentation are blocked for proxy-selected traffic. The relay must preserve the datagram payload unchanged, including DNS IDs, so the originating socket/application can correlate concurrent replies. A relay alias collision with another original flow is rejected as a bounded setup failure; aliases are never shared because reverse routing would become nondeterministic.

## 9. Packet Parsing Boundaries

Release 1 proxy handling supports ordinary safely parseable unfragmented IPv4/IPv6 TCP/UDP. Parsing is bounds-checked before any `unsafe` dereference. IPv6 extension headers are traversed with a strict header-count and byte-length bound until TCP/UDP or an unsupported condition is reached.

Proxy-selected traffic is blocked when it is malformed, fragmented, uses TCP Fast Open data, has an unsafe/unsupported extension chain, uses unsupported SOCKS UDP fragmentation, or exceeds the pinned native frame ABI. `pass` may reinject an otherwise valid frame unchanged without requiring proxy transformation support. Unknown non-TCP/UDP traffic cannot match a TCP/UDP proxy rule and follows later rules/fallback.

## 10. Internal Loop Prevention

The internal self-traffic registry runs before flow lookup and user rules. Before connecting/sending, WinForward explicitly binds and registers each owned socket's local endpoint, protocol, expected remote endpoint(s), and generation. It updates UDP entries atomically when `UDP ASSOCIATE` returns its relay endpoint and tracks local redirect/listener tuples.

The guard is exact to WinForward-owned sockets/tuples and is not a broad exemption for the current process or proxy host. PID/process identity is corroborating evidence, not the sole key. An unrelated application connecting to the same proxy endpoint remains subject to user policy.

## 11. Lifecycle and Failure Handling

Runtime follows an explicit state machine:

```text
Created -> Validated -> PlatformChecked -> DriverOpened -> AdaptersResolved
        -> RelaysReady -> SafetyReady -> ModesApplied -> Running
        -> Stopping -> ModesRestored -> Closed
```

Startup order is deliberately transactional:

1. Parse/validate and compile configuration.
2. Check supported Windows/x64 and elevated token.
3. Load/validate native DLL and open driver.
4. Resolve adapters and snapshot their exact existing modes.
5. Allocate bounded pools/queues and start local listeners/relay infrastructure.
6. Initialize loop-prevention registry.
7. Apply required tunnel modes.
8. Start capture pumps.

Any partial mode application rolls back already changed adapters. Graceful shutdown stops new flow claims, stops capture, gives queued packets one terminal disposition, closes proxy flows, restores exact prior adapter modes, unregisters events, frees unmanaged pools, closes the driver, and exits. Existing proxy connections are terminated safely; they are not promised to continue after shutdown.

Fatal processing failure stops capture, drops undecided/proxy-owned queued data, closes proxy bridges, performs handled cleanup, logs a redacted critical event, and exits nonzero. Release 1 accepts only `block` for proxy-unavailable and processing-failure action fields. Uncatchable process termination or power loss cannot be guaranteed by a foreground process; release claims cover orderly shutdown and handled failures.

## 12. Performance and Concurrency

- Use fixed/unmanaged packet batches and buffer pools in capture/transform paths.
- Avoid per-packet LINQ, strings, exceptions, reflection, and heap allocation.
- Parse with `ReadOnlySpan<byte>`/`Span<byte>` or validated unsafe pointers.
- Precompile policy values and keep runtime config immutable.
- Use sharded flow tables or another measured contention strategy; do not optimize beyond benchmarks prematurely.
- All setup queues and socket relay buffers are bounded and expose counters for saturation/timeouts.
- Use asynchronous sockets and backpressure for TCP; keep blocking DNS/SOCKS setup off capture workers.
- Analyzer packages are centrally versioned and enabled for production code; intentional hot-path suppressions require a localized justification.

## 13. Test Seams and Validation

Pure modules are tested on any development OS:

- configuration parsing/validation/redaction;
- rule order, AND/OR semantics, process/path matching, CIDR/port parsing;
- Ethernet/IP/TCP/UDP parsing, bounds, rewriting, checksums, fragmentation rejection;
- SOCKS5 framing/state machines for IPv4/IPv6/domain and RFC 1929;
- flow aliasing, generation, exactly-once disposition, timeout, queue saturation, and loop registry.

Windows tests use adapters/fakes around process attribution, adapter enumeration, native driver operations, clock, sockets, and packet injection. Hardware integration tests on the supported Windows matrix cover:

- ABI probe and driver open/version diagnostics;
- physical and Hyper-V adapter discovery/recreation;
- pass/block with no duplicate packets and exact mode restoration;
- host and Hyper-V TCP local redirect through SOCKS5 for IPv4/IPv6;
- host and Hyper-V UDP association through dynamic relay endpoints;
- retransmitted SYN, FIN/RST/half-close, UDP idle expiry and failure;
- catch-all proxy rules without self-recursion;
- proxy outage and handled fatal failure with no pass downgrade;
- Native AOT publication and absence of a .NET runtime dependency.

## 14. Key Risks and Release Gates

1. **ABI gate:** managed layouts match the pinned native x64 header/DLL exactly.
2. **Packet ownership gate:** pass/block batches prove exactly-once disposition and no stale adapter mode.
3. **TCP redirect gate:** a focused Windows prototype handles host and Hyper-V IPv4/IPv6 SYN, data, retransmission, FIN/RST, and TCP options through a local listener.
4. **UDP gate:** dynamic relay address/port, setup buffering, reverse mapping, and IPv6 checksums work on a real SOCKS5 server.
5. **Attribution gate:** process misses/ambiguity follow fallback deterministically; tests do not claim perfect NDISAPI PID attribution.
6. **Loop gate:** exact socket registry prevents recursion without exempting unrelated clients of the same endpoint.
7. **Lifecycle gate:** every handled startup/runtime/shutdown path restores prior adapter modes and disposes unmanaged/socket state.

If the TCP redirect prototype fails on the supported WinpkFilter release, implementation returns to planning. Adding a WFP/kernel redirect mechanism would be a material scope change and requires explicit approval.
