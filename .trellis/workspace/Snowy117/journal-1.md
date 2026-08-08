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

