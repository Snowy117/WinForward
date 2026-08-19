# Implementation Plan: Performance Hotspots

## Ordered Checklist

1. [x] Freeze the current baseline: run `dotnet build -c Release`, `dotnet test -c Release --no-restore`, and add/execute the release benchmark harness for parser, flow-table, dispatcher, relay, logging, and copy paths.
2. [x] Record the benchmark schema and workload parameters in the task research/report; include frame sizes, flow/session cardinality, warmup/count, runtime/OS, and whether the result is managed-only or Windows hardware.
3. [ ] Verify the pinned NDISAPI exports and exact ABI for event signaling, batch reads, and reusable buffer ownership. Do not edit imports until the export and structure evidence is recorded.
4. [ ] Implement the capture-path candidate: event/bounded wait, bounded batch drain if supported, and per-pump native storage reuse. Preserve the global mutable-driver serialization until a hardware-backed alternative is proven.
5. [ ] Add capture/native-path correctness tests for cancellation, empty queue, queue drain, adapter enumeration handle, direction flags, metadata flags, frame bounds, and sibling pump failure. Run the benchmark and Windows pass smoke; rollback if p99 or packet correctness regresses.
6. [x] Remove redundant packet materialization in the UDP path using an owner-aware offset/length view; introduce bounded reinjection buffer reuse only where ownership remains explicit. Re-run protocol, checksum, malformed-frame, and frame-cap tests.
7. [x] Benchmark parser address allocations. Only if material, introduce a value-type address/key representation and keep `IPAddress` materialization at required API boundaries. Add hash/equality and IPv4/IPv6 scope tests.
8. [x] Add the canonical transport-tuple index to `FlowTable`; preserve observing versus non-observing lookup behavior and atomic claim semantics. Add cardinality and concurrent admission benchmarks/tests proving absent-flow lookup no longer scans twice.
9. [x] Precompute the policy process-attribution requirement and skip Windows owner-table enumeration when no process selector can match. Add host/forwarded and selector/no-selector tests.
10. [x] Index self-traffic wildcard candidates without changing exact/reverse/wildcard semantics. Add cardinality/contention measurements and token-disposal tests.
11. [x] Bound and pool UDP session receive storage using the reinjectable frame cap. Add setup-failure, expiry, cancellation, coordinator-disposal, truncation, and pool-return tests; run 1/100/1000-session memory measurements.
12. [ ] Rework TCP relay timeout lifecycle to eliminate per-I/O linked CTS/timer creation while preserving stall timeout, external cancellation, half-close, sibling cancellation, and fault observation. Run chunk-size throughput/allocation benchmarks.
13. [x] Guard disabled trace event construction at high-frequency call sites. Verify exact event output, field order, privacy, threshold filtering, and logger-failure isolation.
14. [ ] Run the full quality gate: `dotnet build -c Release`, `dotnet test -c Release`, analyzers, and applicable AOT/trim publish checks. Run Windows smoke for host pass, TCP proxy, UDP proxy, and available forwarded traffic.
15. [x] Perform the final comparison against the frozen baseline, document gains/regressions and unmeasured Windows areas, then request the Trellis quality check before task completion.

Items 3-5 remain open: the pinned Windows DLL and hardware are unavailable in this Linux workspace,
so event signaling, batch-read imports, queue-drain behavior, and RTT/packet-correctness smoke were not
implemented or claimed. Item 4 is partial: per-pump native storage reuse is implemented without ABI
changes. Item 7 measured a fixed ~80 B/op address cost; the broader value-type address redesign is
deferred because payload-sized copies and linear lookups were materially larger in this batch.
Item 12's reusable-CTS candidate reduced relay allocation by 68-81% but regressed measured elapsed
time by 3-14% after refinement, so it was rolled back under the measurement gate.
Item 14 is partial: the final Release build passed with zero warnings and all 294 tests passed;
`win-x64` Native AOT publish was blocked because the .NET IL compiler does not support cross-OS
native compilation from Linux, and Windows smoke remains unavailable.
Item 15 is complete for the portable batch: the final comparison, rejected TCP relay candidate,
Trellis quality-check fixes, and Windows-only limitations are recorded in
`research/implementation-results.md`.

## Validation Commands

```text
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64
```

Windows-only validation additionally requires the pinned `ndisapi.dll`, elevated execution, a supported Windows x64 host, and the configured adapter/traffic matrix from `research/performance-hotspots.md`.

## Risky Areas And Rollback Points

- `src/WinForward.NdisApi/NdisApiAbi.cs` and `NdisApiDriver.cs`: ABI/export or handle mistakes can make every native operation fail. Roll back the ABI batch independently.
- `src/WinForward.NdisApi/NdisCapture.cs` and `MultiAdapterCaptureLoop.cs`: wakeup/drain changes can cause packet loss, duplicate reads, or queue starvation. Compare packet counters and p99 before proceeding.
- `CapturePacketProcessor.cs`, `NdisPacketActionExecutor.cs`, `TcpProxyCoordinator.cs`, and `UdpResponseReinjector.cs`: owner reuse can create use-after-return or double-dispose bugs. Keep ownership tests and retain a simple-copy fallback during development.
- `IpUdpPacket.cs`, `Socks5Udp.cs`, and `UdpFrameBuilder.cs`: view/buffer changes must preserve payload boundaries and frame-cap fail-closed behavior.
- `Domain.cs`, `TcpRedirectTable.cs`, `UdpAssociations.cs`, and `SelfTrafficRegistry.cs`: index changes must preserve touch/expiry, reverse routing, collision handling, and lock contracts.
- `ProcessAttribution.cs`: skipping attribution is valid only when policy proves process identity cannot affect a decision.
- `TcpProxyRelay.cs`: timeout lifecycle changes can alter half-close and stall semantics; keep the existing relay tests and add cancellation/fault timing coverage.
- `RuntimeLogging.cs` and high-frequency callers: logging optimization must not alter disposition, privacy, or stable event format.

## Review Gates Before `task.py start`

- [x] `prd.md` contains no unresolved user-owned decision and has passed the convergence pass.
- [x] `design.md` and this `implement.md` agree on scope, ordering, compatibility, and rollback.
- [x] `implement.jsonl` and `check.jsonl` contain real spec/research entries, not seed rows.
- [x] The latest planning summary is presented to the user and explicitly approved.
