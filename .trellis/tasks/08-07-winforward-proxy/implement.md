# WinForward Implementation Plan

## Current Progress (2026-08-07)

The hardware-independent foundation is implemented and verified: configuration/policy validation, packet and SOCKS5 framing, pinned NDISAPI/IP Helper ABI seams, transactional lifecycle primitives, flow/UDP association ownership, self-traffic guards, and concurrent UDP burst tests. The remaining release gates are Windows-specific capture bridging, complete adapter identity/change handling, TCP transparent redirect, full UDP packet reinjection, runtime wiring, and Native AOT publication on a supported Windows host. The unchecked items below remain intentionally open.

### Check phase (2026-08-07, Win11 host 192.168.100.2 via WinRM)

- Linux + Windows `dotnet build -c Release`: 0 warnings / 0 errors (all four analyzers, TreatWarningsAsErrors).
- Unit tests: 59/59 on Linux and Windows (check phase added 19 tests: config-validation branches, AND/OR semantics, IPv6 packet handling, adapter correlation).
- Native AOT publish on Win11 (SDK 10.0.102, global.json patched on the Windows copy only) produces a working `WinForward.exe` (~2.6 MB).
- AOT smoke tests on Win11 (WinpkFilter driver 3.6.2.1 running, ndisapi.dll 3.6.1 sidecar from tools_bin_x64.zip): `validate` accepts the example config (exit 0) and rejects 10 hand-crafted invalid configs with field-pathed diagnostics (exit 1); `adapters` enumerates 5 MSTCP-bound adapters with stable GUID + friendly name + internal name (exit 0); missing ndisapi.dll fails cleanly with an actionable diagnostic (exit 1).
- Fixed during check: adapter identity correlation is GUID-primary (NDISAPI internal name `\DEVICE\{GUID}` == `NetworkInterface.Id`) with MAC as sanity fallback. The previous MAC-only correlation always failed on real hosts because NDIS filter drivers (WFP LWF, WinpkFilter LWF, Npcap, QoS) clone the physical MAC across multiple `NetworkInterface` entries.

### Run milestone 1 (2026-08-07, pass/block wired and hardware-verified)

- `run` is now end-to-end: config -> platform/elevation -> driver -> adapter-scope resolution (missing/ambiguous selector = startup failure, exit 1) -> self-traffic registry -> transactional tunnel modes -> per-adapter capture pumps -> FlowDispatcher (process attribution on host flows) -> pass/block executor. Exit codes: 0 clean / 1 config-adapter-driver / 2 usage-platform / 3 runtime failure. 78/78 tests on Linux + Win11, AOT publish OK.
- Win11 hardware matrix (all green): pass-through 100/100 ICMP 0% loss no DUP + TCP OK; block rule kills TCP 5985 while ICMP and out-of-scope adapter unaffected; proxy rule fails closed (drop + rate-limited warn); Ctrl+C exits cleanly with exact mode restoration (traffic unaffected afterwards); process attribution blocks curl.exe while powershell passes.
- Fixed during hardware bring-up: reinjection requests must use the enumeration handle from `GetTcpipBoundAdaptersInfo`; the captured buffer's `m_hAdapter` is a different kernel pointer and is rejected with ERROR_INVALID_PARAMETER (87). NDISAPI imports now use `SetLastError = true` and send failures log native error + frame context. Contract recorded in `.trellis/spec/backend/windows-ndisapi.md`.
- Known: polling pump adds ~5-15 ms RTT (event-driven `SetPacketEvent`/`ReadPackets` batch is the upgrade path); adapter-list change handling deferred (seam at `CaptureAdapterScopeResolver`); proxy actions remain fail-closed-blocked until the TCP redirect / UDP relay milestones.

## Execution Strategy

Build WinForward incrementally behind hardware-independent seams. Do not attempt the full proxy runtime before proving the x64 NDISAPI ABI and local TCP redirect on Windows. Each phase must leave the solution building and its applicable tests passing.

## 1. Bootstrap the .NET Solution

- [ ] Add `global.json` pinned to the .NET 10 SDK roll-forward policy.
- [ ] Add solution/projects for CLI, Configuration, Core, Protocols, Windows, NdisApi, Runtime, unit tests, Windows tests, and integration tests.
- [ ] Centralize build properties: `net10.0-windows`, C# 14, nullable, implicit usings policy, warnings, deterministic build, unsafe, Native AOT compatibility, and `win-x64` publish settings.
- [ ] Centralize the four requested analyzer package versions and apply them with `PrivateAssets=all`.
- [ ] Add editor/analyzer configuration only for deliberate project conventions; do not bulk-disable analyzer categories.
- [ ] Add a minimal AOT-compatible CLI command router and stable exit-code definitions.

Validation:

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64
```

## 2. Implement Configuration and Pure Policy Core

- [ ] Define raw JSON DTOs and source-generated serializer context.
- [ ] Reject unknown JSON fields and retain omitted-versus-empty array semantics.
- [ ] Implement redacted structured validation diagnostics.
- [ ] Normalize/validate SOCKS5 servers, credential encoded lengths, rule actions/references, process selectors, CIDRs, ports/ranges, protocols, families, fallback, and block-only failure fields.
- [ ] Build the immutable runtime policy snapshot and server lookup.
- [ ] Implement first-match evaluator with AND-across-fields / OR-within-field semantics.
- [ ] Test invalid inputs, first-match behavior, fallback, exact process/path comparison, and secret redaction.

Gate: all configuration and policy tests pass without Windows or native dependencies.

## 3. Implement Protocol Parsing and SOCKS5 Framing

- [ ] Add bounds-checked Ethernet II, IPv4, bounded IPv6 extension-header, TCP, and UDP views.
- [ ] Add endpoint/L2 rewrite operations and IPv4/TCP/UDP checksum algorithms.
- [ ] Detect fragments, TCP Fast Open data, malformed lengths, unsupported extension chains, and frame-capacity violations.
- [ ] Implement SOCKS5 greeting, NO AUTH, RFC 1929, CONNECT, UDP ASSOCIATE, response parsing, and UDP request/response framing for IPv4/IPv6/domain.
- [ ] Use fixtures and property/fuzz-style bounds tests for truncated/malformed packets.

Gate: packet transforms/checksums and SOCKS5 state machines pass pure tests with no hot-path allocations established by focused benchmarks where practical.

## 4. Pin and Validate the NDISAPI ABI

- [ ] Select and record the exact WinpkFilter/NDISAPI release, header commit, expected DLL/driver versions, and frame ABI.
- [ ] Implement source-generated C ABI imports, SafeHandle, packed blittable structures, fixed buffers, adapter modes, events, and batched read/send calls.
- [ ] Add controlled native DLL resolution and actionable startup error classification.
- [ ] Add the native x64 ABI probe and compare every used managed size/offset.
- [ ] Implement unmanaged packet slabs/leases and exactly-once terminal disposition guards.

Gate (Windows x64): ABI comparison passes and driver open/enumerate/close smoke test succeeds. Stop and return to design on any unexplained layout mismatch.

## 5. Implement Platform Checks, Adapter Discovery, and Process Attribution

- [ ] Check supported Windows version, x64 architecture, and elevated administrator token before filter state changes.
- [ ] Correlate NDISAPI adapters with IP Helper GUID/LUID/permanent/friendly identities.
- [ ] Implement `adapters` output and config selector resolution, including missing/ambiguous diagnostics.
- [ ] Register adapter-list change handling and fail-closed resolution-loss behavior.
- [ ] Implement IPv4/IPv6 TCP/UDP owner-table attribution and full image-path lookup.
- [ ] Cache by PID plus process creation time; define bounded refresh/retry and unknown/ambiguous results.

Gate (Windows): physical and Hyper-V adapters are listed and selectors behave exactly; process attribution tests cover normal, missed, wildcard UDP, inaccessible, exited, and PID-reused processes.

## 6. Implement Transactional Capture with `pass` and `block`

- [ ] Implement lifecycle coordinator and exact prior-mode snapshots.
- [ ] Start bounded capture pumps only after safety state and rollback hooks are ready.
- [ ] Parse/classify packet origin and create canonical flow keys.
- [ ] Implement exactly-once pass reinjection matrix and block consumption.
- [ ] Add flow decision caching/aliases so routed traffic observed on another adapter is not re-evaluated.
- [ ] Implement cancellation, queue saturation, handled fatal failure, and exact mode restoration.

Gate (Windows/Hyper-V): pass produces no duplication, block drops, routed flows make one decision, and all startup/shutdown failure injections restore modes.

## 7. Implement Internal Socket Registry and Flow Runtime

- [ ] Add exact WinForward-owned socket tuple registration before connect/send.
- [ ] Track local listener/redirect tuples and dynamic SOCKS UDP relay endpoints.
- [ ] Ensure internal guard runs before user policy without broad endpoint/process exemption.
- [ ] Implement atomic flow claim, translated aliases, bounded setup queues, generation, idle/close expiry, and counters.
- [ ] Test catch-all proxy policy, unrelated application using the same proxy endpoint, multiple servers, changing DNS results, and UDP relay port differences.
- [ ] Test concurrent same-process DNS-style datagrams on one source socket, multiple destinations from one socket, distinct source sockets, and deliberate local-endpoint reuse; assert deterministic flow/association ownership and no response cross-wiring.

Gate: no recursive self-interception and no unrelated endpoint bypass.

## 8. Prove and Implement TCP Local Redirect

- [x] **8a (done, hardware-independent):** protocol-layer TCP endpoint rewrite primitive `PacketChecksums.TryRewriteTcpEndpoints` for IPv4/IPv6 — rewrites only addresses/ports/checksums, recomputes IPv4 header + TCP checksums (TCP never zero-inverts, per RFC 9293), leaves seq/ack/flags/options/payload untouched. Covered by `TcpEndpointRewriteTests` (15 tests incl. IPv6 Hop-by-Hop→TCP success path). 93/93 suite green. Feeds 8b/8c; not yet wired into the runtime.
- [x] **8b (done, hardware-independent):** runtime TCP redirect coordinator behind abstraction seams (`TcpProxyCoordinator` + `TcpRedirectTable` + `ITcpRedirectListenerFactory`/`ITcpProxyRelayFactory`/`ITcpRedirectInjector`), mirroring `UdpProxyCoordinator`. Exactly-once flow claim, translated-tuple aliasing, fail-closed teardown on every setup/rewrite/inject/relay failure, self-traffic registration of the listener tuple, SYN rewrite + reverse-rewrite via 8a. Parent found and fixed a TOCTOU: concurrent SYN racers past the pre-claim fast path got a duplicate-key crash; fixed by detecting the existing association from `TryClaim` and releasing the redundant listener + re-injecting (reproduced/locked by `ConcurrentSynBurstWithAsyncListenerStaysExactlyOnce` with an async-gap fake). 106/106 suite green. Not yet wired into the live capture path; executor stays fail-closed until 8c.
- [x] **8c (code done, Windows end-to-end pending):** real concrete classes behind 8b seams — `TcpRedirectListenerFactory`/`TcpRedirectListener`/`TcpAcceptedConnection` (real loopback Socket), `TcpProxyRelayFactory`/`TcpProxyRelay` (Socks5ControlConnection CONNECT + bidirectional byte pump with 8KB bounded buffers, upstream socket registered in SelfTrafficRegistry), `TcpRedirectInjector` (wraps IPacketReinjector, SendToMstcp toward the listener). `HandlePacketAsync` routes SYN/reverse/data. Wired into `NdisPacketActionExecutor.ProxyAsync` + `Program.RunCaptureLoopAsync`. Parent found and fixed a reverse-direction bug: the flow classifier sets Local=Source/Remote=Destination, so a reverse packet (listener→client) has Local=listener; the resolver now checks both Local and Remote against the translated-tuple index (locked by `ReversePacketWithClassifierOrientationResolvesToOriginalFlow`). 107/107 suite green. Open for Windows validation: (1) SYN delivery to listener via SendToMstcp, (2) injector direction for reverse/forwarded flows, (3) mid-flow redirect-leg capture assumption.
- [x] **8c (TCP redirect hardware-verified on Win11, host IPv4):** real sockets/SOCKS5/NDISAPI wiring verified end-to-end. `curl http://192.168.77.1/` through a `proxy` rule for curl.exe reaches the gateway via the local SOCKS5 test server (CONNECT 192.168.77.1:80), HTTP 308, 5/5 stable, 0 accept failures, graceful Ctrl+C restores adapters (post-stop traffic unaffected). Key fixes from hardware bring-up: (1) use the official WinpkFilter local_redirect transform — swap MACs + swap IPs + rewrite th_dport to the proxy port, KEEP the client's source port (the redirector only rewrites th_dport, never th_sport), listener binds 0.0.0.0; (2) MSTCP's SYN-ACK in response to an injected SendToMstcp SYN is a reverse packet that must be reversed BEFORE flow lookup/policy — added a dispatcher reverse-hook (HandleReverseIfApplicableAsync) that runs before flow/policy so a reverse packet is never re-evaluated as a new client flow or silently passed; (3) mid-flow client->listener data must be rewritten to the proxy tuple and reinjected (ReinjectExistingFlowDataAsync); (4) drain redundant accepts after the first relay (a retransmitted SYN opens a second listener connection; close it instead of a second relay) — eliminated the accept-failed storm. LoopbackFilter was investigated but is NOT used (the reverse path is handled by the dispatcher hook; the loopback reflection of an injected SYN is drained as a redundant connection). **IPv6 host TCP verified too: `curl http://[fd00:1234:5678:1::1]/` returns 308 via SOCKS5 CONNECT to the IPv6 gateway, 3/3 stable.** **Forwarded-direction fix: reverse injection direction now follows the flow origin — host flows reverse to MSTCP, forwarded flows (VM/remote client) reverse to the origin adapter (SendToAdapter); locked by ForwardedFlowReverseInjectsTowardOriginAdapter / HostFlowReverseInjectsTowardMstcp. 109/109 suite green.** Remaining: Hyper-V forwarded TCP on a host with a real VM (current Win11 has no Hyper-V VM stack, so guest-originated forwarded traffic cannot be exercised here), UDP relay (milestone 9), and the full hardware matrix.
- [ ] Prove host-originated and Hyper-V-originated IPv4/IPv6 initial SYN redirection to a local listener.
- [ ] Preserve tuple mapping, TCP sequence/ACK space, TCP options, SYN retransmission, FIN, RST, and half-close while rewriting endpoints/checksums.
- [ ] Connect accepted local sockets to the selected SOCKS5 server and perform CONNECT for the saved original target.
- [ ] Relay bytes asynchronously with bounded buffers/backpressure and fail-closed setup behavior.
- [ ] Integrate translated/reverse packets with the origin adapter/L2 context and runtime lifecycle.

Gate (release blocking): host and Hyper-V TCP work through SOCKS5 for IPv4/IPv6 under retransmission and orderly/error closure. If this fails, return to planning; do not substitute WFP or a kernel component without approval.

## 9. Implement UDP Local Relay and SOCKS5 Association

- [x] **9a (wired, hardware-independent; hardware pass pending):** UDP relay wired into the capture/executor path. `UdpFrameBuilder` (Protocols, pure) builds Ethernet II + IPv4/IPv6 + UDP frames with RFC 768 checksum inversion; `UdpResponseReinjector` (IUdpResponseSink) rebuilds client-bound response frames (src = real server, dst = original flow local) and injects toward MSTCP (host) or the origin adapter (forwarded); `NdisPacketActionExecutor` gained a `UdpProxyCoordinator?` param — a proxy-decided UDP datagram is parsed for its payload and forwarded via `TrySendAsync` (consumed, never reinjected; parse failure/unsent fails closed); `Program` constructs the coordinator with `Socks5UdpTransportFactory(selfTraffic)` + reinjector (scope adapter MAC from NDISAPI CurrentAddress). **Loop-prevention fix:** `Socks5UdpTransport` now registers `(Udp, localSocketEndpoint, relayEndpoint)` in `SelfTrafficRegistry` when UDP ASSOCIATE returns the dynamic relay endpoint (released on dispose) — catch-all proxy rules never recursively intercept WinForward's own UDP relay traffic. 122/122 suite green. Open for Win11 hardware validation: DNS/QUIC-style bursts, concurrent same-socket datagrams, relay port != SOCKS TCP port.
- [ ] Claim the first datagram and buffer setup traffic within strict packet/byte/deadline limits.
- [ ] Establish and retain per-flow SOCKS5 control and UDP sockets.
- [ ] Register and validate the dynamic relay endpoint.
- [ ] Encode/decode SOCKS5 UDP framing, reject `FRAG != 0`, and restore remote/client/L2 context.
- [ ] Reinject responses toward MSTCP or the origin Hyper-V adapter with correct IPv4/IPv6 checksums.
- [ ] Implement idle expiry, control-channel failure, generation reuse, and teardown.

Gate: host and Hyper-V UDP work through SOCKS5 for IPv4/IPv6, including DNS/QUIC-sized bursts within the documented unfragmented limits and a relay port different from the SOCKS TCP port.

## 10. Complete CLI, Diagnostics, Documentation, and Hardening

- [ ] Complete `validate`, `adapters`, and `run --config` behavior and exit codes.
- [ ] Add structured, rate-limited operational logs and counters with credential redaction.
- [ ] Document configuration, no implicit rules, pass/forwarding/NAT responsibility, unsupported proxy packet classes, Native AOT publishing, native DLL/driver deployment, administrator requirement, and graceful shutdown.
- [ ] Add example configurations for process proxying, Hyper-V adapter proxying, explicit DNS policy, pass fallback, and leak-prevention block fallback.
- [ ] Run analyzer, build, test, publish, dependency, performance, supported-Windows, Hyper-V, proxy-outage, and lifecycle matrices.

Final validation:

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64
```

On a supported elevated Windows/Hyper-V host, additionally run the documented ABI, adapter, pass/block, TCP, UDP, loop-prevention, failure, and cleanup integration suites.

## Risky Boundaries and Rollback Points

- **Native ABI:** do not continue past the ABI phase until the exact header/DLL layout passes; revert interop changes rather than guessing packing.
- **Adapter mode:** every mode write must have an idempotent rollback registered first; integration tests must inspect post-exit mode.
- **Packet ownership:** never suppress disposition guards to improve throughput; optimize only after exactly-once tests pass.
- **TCP redirect:** keep the proof-of-concept isolated until its release gate passes. Failure returns the task to planning.
- **Flow/queue limits:** no unbounded queue or table is accepted; fail closed under saturation.
- **Self traffic:** avoid broad static proxy-host exemptions. Revert if unrelated applications bypass policy.
- **Secrets:** no diagnostic/test snapshot may contain configured credentials.

## Pre-Start Checklist

- [ ] `prd.md` has completed its convergence rewrite and has no blocking open questions.
- [ ] `design.md` and this implementation plan have been reviewed.
- [ ] `implement.jsonl` and `check.jsonl` contain real spec/research context.
- [ ] The user explicitly approves the latest planning summary after these artifacts are complete.
- [ ] Only then run `task.py start`; implementation approval is not inferred from earlier task-creation consent.
