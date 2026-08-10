# Journal - Snowy117 (Part 1)

> AI development session journal
> Started: 2026-08-07

---

## 2026-08-07 — winforward-proxy check phase

- Verified build/tests on Linux and on the Win11 host (192.168.100.2 via evil-winrm-py, source copy at `C:\Users\Neko\WinForward`, SDK 10.0.102 with global.json patched on the copy only): 0 warnings/0 errors, 59/59 tests, Native AOT publish OK.
- AOT smoke on Win11 (driver 3.6.2.1 running, ndisapi.dll 3.6.1 sidecar): `validate` good/10 bad configs behave; `adapters` lists 5 adapters with stable GUID + friendly name after fixing MAC-only correlation (filter drivers clone MACs across ~6 NetworkInterface entries; GUID-primary correlation via `\DEVICE\{GUID}` == `NetworkInterface.Id`).
- trellis-check sub-agent (deepseek-v4-flash) added 19 tests and fixed adapter correlation; remaining gaps are the documented Windows-gated phases (TCP redirect, runtime wiring, adapter-change handling).

## 2026-08-07 — run milestone 1: pass/block on hardware

- trellis-implement (deepseek-v4-flash) wired `run` end-to-end (scope resolution, transactional tunnel modes, capture pumps, dispatcher with process attribution, pass/block executor); 78/78 tests.
- Hardware bring-up found a real driver contract: reinjection requests must use the enumeration handle, not the captured `m_hAdapter` (error 87 otherwise). Isolated with a scratch 3-variant harness (sendtest); fixed in `NdisCapture.cs`; spec updated.
- Win11 matrix all green: pass (100/100 ping, no DUP), block, proxy fail-closed + warn, Ctrl+C clean restore, process attribution (curl blocked / powershell passed), missing-adapter selector exit 1.
- WinRM test rig: configs + ctrlc.ps1 (GenerateConsoleCtrlEvent graceful stop) + watchdog.ps1 (90s auto-stop safety) under `C:\Users\Neko\wf-tests`; publish at `C:\Users\Neko\pub`. Test adapter = Ethernet 8 (192.168.77.2), WinRM stays on Ethernet (192.168.100.2).

---

## 2026-08-08 — milestone 8a: TCP endpoint rewrite primitive (hardware-independent)

- Split milestone 8 (TCP Local Redirect) into 8a protocol-layer / 8b runtime state machine / 8c Windows PoC, doing the hardware-independent parts first.
- trellis-implement (deepseek-v4-flash) added `PacketChecksums.TryRewriteTcpEndpoints` (IPv4/IPv6 addr+port rewrite, IPv4 header + TCP checksum recompute; only addresses/ports/checksums touched — seq/ack/flags/options/payload inviolate per design §8). TCP checksum stores folded result verbatim (no UDP-style 0→0xFFFF invert, RFC 9293). Refactored `TryFindIpv6Udp` into shared `TryFindIpv6Transport(targetNextHeader)` — byte-identical for UDP.
- trellis-check (two independent passes) verdict ALL PASS / NO BLOCKERS. Residual risks all minor: R1 zero-inversion test non-adversarial, R2 pre-existing 256-byte ext cap, R3 no IPv6-ext-then-TCP success test.
- Parent closed R3 by adding `Ipv6HopByHopExtensionThenTcpRewritesSuccessfully` (Hop-by-Hop next-header chain 0→6). Suite now 93/93, 0 warnings.
- spec(quality-guidelines): recorded the endpoint-rewrite contract, TCP/UDP checksum divergence rule, fragment-mask mirroring rule, and packet-rewrite test requirements.
- Committed as ccdf036. Next: 8b (runtime TCP state machine — SYN claim, translated tuple aliasing, local listener, relay lifecycle, reverse rewrite with fake-reinjector tests), then 8c Windows PoC + hardware verify.

---

## 2026-08-08 (cont.) — milestone 8b: TCP redirect coordinator (hardware-independent)

- Planning: trellis-research (glm-5.2) repeatedly stalled on long-form generation (read all context, then hung) across 2 attempts; orchestrator interrupted and authored the 8b plan directly from a full read of UdpProxyCoordinator/UdpAssociations/FlowDispatcher/Core types. Key decision: 8b = coordinator + table + 3 abstraction seams (ITcpRedirectListenerFactory/ITcpProxyRelayFactory/ITcpRedirectInjector), mirroring the UDP pattern; 8c = real sockets + NDISAPI + SOCKS5 end-to-end.
- trellis-implement (deepseek-v4-flash) delivered coordinator + seams + table + 12 tests; 105/105. (One interruption mid-generation; resumed with explicit continuation prompt and finished cleanly.)
- trellis-check (glm-5.2) stalled identically to the planning agent on report generation (3rd glm-5.2 stall this session); orchestrator interrupted and performed the review directly.
- Parent review found a real TOCTOU: HandleSynAsync's pre-claim TryResolveByOriginal fast path returns empty for all concurrent SYN racers before any TryClaim runs; later racers got the existing association from TryClaim but proceeded to _sessions.Add → ArgumentException (duplicate key). The synchronous fake listener masked it. Fixed by detecting the existing association (translated-tuple mismatch) and releasing the redundant listener + re-injecting.
- Added ConcurrentSynBurstWithAsyncListenerStaysExactlyOnce with a Task.Yield() gap in the fake listener factory to reproduce; verified it FAILS without the fix (ArgumentException) and PASSES with it. This is the load-bearing concurrency test — the synchronous ConcurrentSynBurst test was non-load-bearing.
- spec(quality-guidelines): recorded the proxy-coordinator structural contract and the concurrent-initial-packet exactly-once rule (must force an async allocation gap in fakes or the concurrency test is non-load-bearing).
- glm-5.2 reliability note: on this harness it reliably completes short targeted outputs but repeatedly hangs on multi-section long-form generation (planning doc, review report). deepseek-v4-flash handles long code generation reliably. For 8c, prefer deepseek for implementation and do structural review inline rather than delegating long-form review to glm-5.2.
- Committed as 69094a7. Suite 106/106, 0 warnings. Next: 8c Windows PoC (real listener socket binding, SOCKS5 CONNECT relay pump, NDISAPI reinjection wiring into the capture pump + executor, Hyper-V L2 context, end-to-end hardware gate on Win11).

---


## 2026-08-08 (cont.) — 8b independent check + test hardening

- trellis-check (glm-5.2, fresh pass after model availability restored) reviewed 8b. High-quality review: reviewer personally neutered the TOCTOU fix and found the locking test (yieldOnce) was scheduler-dependent and did NOT reliably reproduce the race. Verdict NEEDS FIXES on two non-behavioral items: (1) test determinism, (2) TryFind locking inconsistency.
- Fix 1: replaced yieldOnce fake with a TaskCompletionSource-gated factory (all N callers block in CreateAsync until the last arrives, proving all passed the empty-table fast path before any claim). Added internal ConcurrentLoserCount counter; test asserts == N-1. Neutering now deterministically fails with ArgumentException at _sessions.Add. Added InternalsVisibleTo for the counter.
- Fix 2: TcpRedirectTable.TryFind changed from static lock(table) to instance lock(_gate), matching UdpAssociationTable.
- spec updated: barrier/TCS-gated concurrency-test technique (Yield-only is non-load-bearing); single-gate-lock discipline for association tables.
- Committed as 60986e2. Suite 106/106, 0 warnings. glm-5.2 note: when it completes (with explicit output-nudging after stalls), its reviews are rigorous and worth waiting for; the stall-then-nudge pattern is reliable.

## 2026-08-08 (cont.) — 8c: TCP redirect hardware-verified on Win11 (RELEASE GATE PASSED)

- WinRM PTY via mkfifo + bg_jobs (evil-winrm-py upload/download/menu). Discovered WinRM shells die on long commands; workaround = schtasks scheduled task for publish (independent of the WinRM session), poll a done-marker file.
- First sync failure: tar.gz extraction on Win11 silently dropped several new files (TcpProxyRelay.cs etc.) and kept stale PacketChecksums.cs — the swapped SYN frame had a corrupted TCP header (extra 0x0000, data offset 0). Lesson: after uploading an archive, verify per-file freshness (Select-String on key symbols) before trusting the build.
- global.json on Win11 requested 10.0.109 (local) but only 10.0.102 installed — patched the Windows copy only (journal entry from earlier check phase confirmed this pattern).
- The big one: dst->loopback local redirect produced a byte-perfect SYN (verified IP/TCP checksums) that MSTCP silently ignored. Pulled the official local_redirect.h + socksify.cpp + simple_packet_filter.h: the real transform is swap MACs + swap IPs + th_dport=proxy_port, KEEP th_sport (client's original port), listener binds 0.0.0.0, and the packet is reinjected toward MSTCP (ON_RECEIVE). Rewrote coordinator to this contract.
- Next failure: MSTCP's SYN-ACK (response to the injected SYN) was never reversed — it was re-evaluated by policy (process attribution can't match the injected tuple) and passed to the wire, so the client never got its SYN-ACK. tshark on the host (Wireshark installed) was decisive: it showed the injected SYN accepted (SYN-ACK emitted) but nothing reversed. Fix: dispatcher-level reverse hook (HandleReverseIfApplicableAsync) runs BEFORE flow lookup/policy, keyed by proxy port (TryResolveByProxyPort — reverse packets carry the client's local IP as source, port is the stable discriminator).
- Flooded accept-failed SocketExceptions after each connection: a retransmitted SYN opens a second connection on the same listener. Fix: after the first relay, drain and close redundant accepts (DrainRedundantConnectionsAsync). accept failed: 0 after the fix.
- Final: 5/5 curl http://192.168.77.1/ -> HTTP 308 via SOCKS5 (CONNECT 192.168.77.1:80), 0 accept failures, graceful Ctrl+C exit 0, post-stop traffic normal. LoopbackFilter (0x20) investigated but NOT used.
- Committed d45051f. Suite 107/107. Remaining: IPv6 host TCP, Hyper-V forwarded TCP, UDP relay (milestone 9), full hardware matrix.

## 2026-08-08 (cont.) — IPv6 host TCP verified + forwarded direction

- IPv6 host TCP verified on Win11: `curl http://[fd00:1234:5678:1::1]/` via proxy rule -> 308 via SOCKS5 CONNECT to IPv6 gateway, 3/3 stable. No code change needed — 8a's TryRewriteTcpEndpoints already handles IPv6, and the swap/reverse/mid-flow machinery is family-agnostic.
- Discovered the Win11 test host has NO Hyper-V VM stack (Get-VM / Msvm_ComputerSystem empty; the "Microsoft Hyper-V Network Adapter" interfaces are leftovers/host-is-VM artifacts). Guest-originated forwarded traffic cannot be exercised on this host — the forwarded path needs a real VM host for the hardware matrix.
- Forwarded-direction fix: HandleReverseAsync always injected toward MSTCP, which is wrong for forwarded flows (client behind a VM/remote adapter — reverse must go back to the origin adapter). Changed ITcpRedirectInjector contract isOnSend -> towardMstcp; coordinator passes origin == Host. Locked by 2 new tests. 109/109.
- Regression: IPv4 + IPv6 host TCP both 3/3 308 after the change. Committed 49a568f.
- Next: milestone 9 (UDP relay). UdpProxyCoordinator/UdpAssociations/Socks5UdpTransport already exist from the foundation; needs the UDP packet rewrite/reinject path wired into the capture executor (like TCP) + hardware verification on Win11 (DNS/QUIC-style bursts, concurrent same-socket datagrams, relay port != SOCKS TCP port).

## 2026-08-09 — milestone 9: UDP relay wired + hardware-verified (Win11)

- trellis-implement wired UDP relay: UdpFrameBuilder (pure frame builder, RFC 768 0->0xFFFF), UdpResponseReinjector (IUdpResponseSink), executor UDP branch (parse payload -> TrySendAsync, consumed), Program wiring with Socks5UdpTransportFactory(selfTraffic) + scope-adapter MAC. Self-traffic fix: relay registers (Udp, 0.0.0.0:local, relayEndpoint), released on dispose. 122/122.
- Hardware bring-up (Win11, local python SOCKS5 server with UDP ASSOCIATE support):
  - python's DNS query format bug (bytes([1]) vs bytes([7]) for "example") — fixed the verify script; burst script was correct.
  - Took many tshark captures to isolate: the local SOCKS5 server's own forwarded queries (forward_one target sockets) get re-caught by WinForward and re-proxied -> ASSOCIATE storm (819+). Self-traffic protects WinForward's own sockets, not the SOCKS5 server's. This is a TEST-HARNESS artifact; a remote SOCKS5 avoids it.
  - Two real fixes found: (1) UDP proxy flow's reverse datagram (reinjected response) must pass, not re-proxy (FlowDispatcher IsReverseOf check) — else the response loops forever; (2) SelfTrafficRegistry must wildcard-match Any-bound sockets by port+remote (the relay socket binds 0.0.0.0 but emits routing-chosen src IP).
  - Clean hardware proof: nslookup example.com 192.168.77.1 through proxy rule -> UDP ASSOCIATE + relay response 3/3.
- Committed 953c92d. Suite 123/123. Remaining: Hyper-V forwarded UDP (needs real VM host), IPv6 UDP, full matrix.

## 2026-08-09 (cont.) — milestone 10: docs, examples, hardening

- README.md written (configuration contract, no-implicit-rules, pass/forwarding/NAT responsibility, unsupported packet classes, AOT publish + ndisapi.dll/driver deployment, admin, graceful shutdown).
- Six example configs (process-proxy, hyperv-adapter-proxy, dns-policy, dns-proxy, pass-fallback, block-fallback); ExampleConfigurationsAllValidate locks them (124/124).
- Proxy-outage hardware verification on Win11: stopped the local SOCKS5 server, curl via proxy rule -> blocked (CODE=000) with "TCP redirect relay setup failed; releasing the flow alias" (fail-closed, no pass downgrade). Graceful Ctrl+C exit 0, post-stop traffic normal (308). Committed as 'docs: add README, example configs, and example-config validation'.

## 2026-08-12 — release 0.2.0 (first complete-functionality release)

- Review-fix pass committed (98fbc62): CRITICAL TCP upstream loop-prevention registration (Socks5ControlConnection binds wildcard + onSocketReady before SYN, both TCP CONNECT and UDP ASSOCIATE control connections register), adapterId+adapterName same-adapter startup error, IdleExpirySweeper wired (flow/tcp/udp RemoveExpired + UdpAssociationTable now live for relay-alias collision), SetDllImportResolver loading ndisapi.dll only from AppContext.BaseDirectory, SOCKS5 REP status surfaced, O(1) proxy-port index, SetLastError on open-path imports. 129/129 tests, 0 warnings/0 errors.
- Released WinForward-0.2.0-win-x64.zip to dist/: Native AOT exe (4.0MB, published on Win11 via evil-winrm-py + schtasks), ndisapi.dll 3.6.1 sidecar, updated package README (0.2.0 feature sheet), all 6 example configs. Smoke-tested on Win11: validate exit 0, adapters lists MSTCP-bound adapters with GUIDs, PUBLISH_EXIT=0.
- WinRM notes for future releases: evil-winrm-py is interactive-only (no commands= arg); the reliable pattern is a self-contained bash call (mkfifo in/out + printf command > fifo + timeout cat out) per command; upload runs in the same session (sleep 8 for connect, sleep 20 for a 6.7MB upload); long builds must run via schtasks /Run and poll a done-marker file (WinRM shells die on long commands); printf swallows \e/\U so paths with single backslashes must be sent base64-encoded (powershell -EncodedCommand) or the command is corrupted; the Windows copy of global.json must be patched to the installed SDK (10.0.102) before publish.

## 2026-08-12 (cont.) — 0.2.1: SOCKS5 success-prefix regression fix + release

- User reported `[warn] UDP proxy handling failed: IOException: SOCKS5 server returned an invalid reply prefix` with a socks5 server that was fine. Root cause: the 0.2.0 REP-status fix made `ReadEndpointReplyAsync` validate the 5-byte VER/REP prefix with `Socks5Messages.TryParseReply`, which requires >= 8 bytes for a success reply. Every successful SOCKS5 reply (TCP CONNECT, UDP ASSOCIATE) was rejected and proxy flows failed closed. Hardware tests before the fix never re-ran the UDP path after it.
- Fix: added `Socks5Messages.TryParseReplyPrefix` (validates VER=5 + REP status only, no length requirement). `ReadEndpointReplyAsync` uses it on the 5-byte prefix, then still reads the full reply (via TryGetReplyLength) and parses the bound address. Locked by `Socks5SuccessPrefixIsAcceptedByPrefixParser`. 130/130 tests.
- Committed 64408ff. Rebuilt 0.2.1 on Win11 (130/130, PUBLISH_EXIT=0). Hardware verification via `schtasks /RL HIGHEST` (a plain schtasks task runs non-elevated and WinForward exits with administrator_required): SOCKS5 log showed `UDP ASSOCIATE requested -> UDP relay bound -> forward 46B to 192.168.77.1:53` with no invalid-prefix errors.
- LESSON (cost a network outage): running WinForward with a catch-all UDP proxy rule on the WinRM host itself proxies the WinRM control traffic and drops the WinRM connection (No route to host; ~6 min unreachable until manual reboot). For WinRM-host testing always scope rules with `remoteCidr: [192.168.100.1/24]` pass for the WinRM subnet so the control session survives, or run at a lower privilege cleverly. 0.2.1 released to dist/.
- Another WinRM lesson: PowerShell `$ErrorActionPreference='Stop'` + `dotnet test 2>&1` triggers a TerminatingError on stderr writes (Fatal error) even when the build/tests pass. Use `$ErrorActionPreference='Continue'` in build scripts and gate on `$LASTEXITCODE`.


## Session 1: WinForward review-driven bug-fix batch (H1-H3, M1-M5, L1-L4) + task archive

**Date**: 2026-08-10
**Task**: WinForward review-driven bug-fix batch (H1-H3, M1-M5, L1-L4) + task archive
**Branch**: `master`

### Summary

Reviewed the WinForward capture/proxy datapath and fixed reviewed functional/UX bugs via the fix-plan workflow (fix plan -> validation subagent -> trellis-implement -> test-authoring subagent -> trellis-check re-verify). Source commit f2a9a4c resolves: H1/M5 TCP-only + address-family gate on the reverse handler; H2 origin-adapter routing for forwarded UDP responses (fail-closed on unresolved origin); H3 direction-correct ON_SEND/ON_RECEIVE injection flags; M1 NormalizeBndAddress; M2 IPv6 ScopeId; M3 frame-cap parameterization sourced from NdisApiAbi.MaximumEthernetFrame; M4 Relaying-not-expired + relay stall timeouts; L1 SOCKS5 connect cap/deadline/socket disposal; L2 Socks5ReplyKind discrimination; L3 bounded accept-error back-off; L4 comment clarity. Test commit fc33351 adds new TcpRedirectInjectorTests.cs plus targeted assertions for every fix. Docs commit 8fae5fb adds fix-plan-2026-08-09.md and review-capture-lifecycle-config-2026.md. Build: 0 warnings/0 errors; suite 149/149. Final trellis-check verdict: APPROVE-WITH-NOTES. Archived tasks 08-07-winforward-proxy and 00-bootstrap-guidelines. Hardware (Hyper-V forwarded TCP/UDP, >30min-idle TCP) remains a supported-Windows-host gate.

### Git Commits

| Hash | Message |
|------|---------|
| `f2a9a4c` | (see git log) |
| `fc33351` | (see git log) |
| `8fae5fb` | (see git log) |

### Status

[OK] **Completed**
