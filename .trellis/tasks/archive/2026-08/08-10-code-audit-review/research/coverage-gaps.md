# Coverage Gaps

The rows below are uncovered behavior or platform gates, not additional confirmed defects. Each has
a recommended seam or hardware procedure so it can become executable evidence.

| Module | Uncovered behavior or branch | Why current tests do not prove it | Recommended gate |
| --- | --- | --- | --- |
| Core | `BoundedSetupQueue` with multiple producers | It has no Runtime consumer and is deliberately single-threaded. | Add ownership/synchronization tests before multi-producer adoption. |
| Core | IPv6 link-local policy matching | Prefix tests do not model route/interface scope selection. | Test with real scoped sockets on Windows. |
| Core | Non-observing `FlowTable.TryGet` future use | Runtime does not use the method for packet observation. | Preserve the existing non-touch assertion when adding a caller. |
| Protocols | IPv4 IHL, TCP/UDP declared-length, and IPv6 extension matrices | Current regressions cover defects but not all accepted/rejected boundaries. | Add table-driven parser and rewriter vectors. |
| Protocols | UDP endpoint rewrite independent checksum oracle | Success tests still use a production checksum helper in part of the oracle. | Add independent Sum/Finish checks for IPv4 and IPv6 rewrite outputs. |
| Protocols | Actual TCP checksum-zero vector | Existing test documents behavior without constructing a computed-zero TCP segment. | Add a generated segment whose checksum folds to zero. |
| Protocols | Captured checksum acceptance/offload policy | Parsers intentionally skip validation but the rationale is not locked. | Document and test checksum-offload tolerance. |
| Protocols | SOCKS RFC 1928/1929 breadth | No complete IPv6/auth boundary/full-reply ATYP matrix. | Add pure frame vectors for all valid layouts and 0/255/256-byte UTF-8 values. |
| Protocols | SOCKS UDP response and budget boundaries | Domain response is dropped before reinjection and header-overhead cap is unspecified. | Define domain-response behavior and test max IPv4/IPv6/domain frame sizes. |
| Protocols/Runtime | Scripted live SOCKS state machine | Existing loopback tests cover selected stalls, not authentication response or malformed success replies. | Add a scripted loopback SOCKS server test harness. |
| NDISAPI | Static P/Invoke call sequence and native errors | Production code calls static imports directly. | Introduce a narrow native-call invoker seam or run the Windows driver matrix. |
| NDISAPI | Shipped DLL ABI, TCHAR mode, and jumbo layout | Linux tests assert only the pinned managed non-jumbo layout. | Compile/run a native sizeof/offsetof probe with the shipped driver/DLL. |
| NDISAPI | Adapter churn, queue/read failure, and metadata flags | No real NDIS driver is present on Linux. | Disable/re-enable a scoped adapter; inspect nonzero `m_Flags`, VLAN, and max frame traffic. |
| Windows | Owner PID reuse between IP Helper snapshot and process lookup | Owner rows do not include process creation time. | Add a native row projection seam or validate conservative behavior on Windows. |
| Windows | Link-local IPv6 attribution | Captured packet endpoints carry no interface scope. | Map adapter identity to interface index before supporting scoped attribution. |
| Windows | Table growth/errors and protected process access | ABI tests cover decoders, not live native calls. | Use injected native-table seam and elevated Windows checks. |
| Capture/runtime | Pump lifetime, blocked handler cancellation, and multi-pump join | `NdisCapturePump`/`MultiAdapterCaptureLoop` lack a controllable driver seam. | Add a fake pump/driver seam plus deterministic blocking-handler tests. |
| Capture/runtime | Sweeper ordering and active-tick disposal | No controlled-time test drives its loop directly. | Inject clock/tick trigger and ordered expiry fakes. |
| Capture/runtime | Real mode snapshots/restoration | Lifecycle tests use an abstract mode controller. | Run Ctrl+C and handled-failure matrix on NDISAPI hardware. |
| TCP redirect | Redundant accepts and live listener behavior | Fakes cover peer checks and ownership but not actual wildcard socket delivery. | Run retransmit/redundant-accept cases through a real SOCKS server on Windows. |
| TCP redirect | Long-idle and real stalled relays | Unit tests cover fault/half-close but not native traffic or 30-minute timeout behavior. | Hardware test SSH-like idle session and one-sided peer stall. |
| UDP SOCKS | Dynamic relay, cross-family control, and reply-domain DNS | Fakes avoid real server resolution/relay socket behavior. | Loopback plus remote SOCKS integration tests. |
| UDP SOCKS | Adapter-map refresh and forwarded Hyper-V response delivery | Reinjector tests use fake handles/MACs and startup snapshot map. | Test with a real VM/vSwitch and adapter change. |
| CLI | Argument grammar and process exit/output | Tests do not invoke `Program.Main` as a process. | Add process-level tests for duplicate/missing/unknown arguments and exit codes. |
| CLI | Outer exception-message redaction | Parser diagnostics are redacted; outer `exception.Message` paths lack a process assertion. | Add failing I/O/native seam tests that inject credential-shaped messages. |
| CLI | `win-x64` Native AOT publish | Cross-OS native compilation is unavailable on this Linux host. | Publish and run the smoke matrix on supported Windows x64. |

`windows-validation.md` converts the hardware rows into a safe, ordered validation procedure.
