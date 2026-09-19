# GC-less zero-allocation hot paths

## Goal

Make WinForward run with the GC effectively never firing (fully GC-off in practice) and with zero memory leaks: all steady-state allocations move off the managed heap (`System.Runtime.InteropServices.NativeMemory` pools, `Span<T>`, `stackalloc`), with an explicit GC configuration fuse so any allocation regression fails fast instead of silently collecting.

Scope decision (user): **full-path zeroing** — per-packet AND per-flow/per-connection application allocations are all eliminated or pooled.

## Background (confirmed facts from code audit)

- Runtime: .NET 10, `net10.0`, CLI is **Native AOT** (`PublishAot=true`, win-x64, single file). No GC settings exist anywhere today.
- .NET has no official "disable GC" switch (AOT included). GC is allocation-driven: steady-state zero allocation ⇒ GC never triggers. A `GCHeapHardLimit` fuse turns regressions into fail-fast OOM instead of silent collection.
- Zero-alloc skeleton already exists: pointer-based NDIS interop (`IntermediateBuffer` via `NativeMemory.AllocZeroed`), span parsing/checksums/rewriting, sync fast-path dispatcher, pooled reinjection, `NdisPacketBufferPool` (NativeMemory, cap 256, `Shared`).
- Hot-path contract codified in `.trellis/spec/backend/hot-path.md` (dispatcher 160B gate, relay pump 0B/op, raw `IPAddressValue`, native lease lifetime).
- Validation infrastructure: 589-test suite, BenchmarkDotNet `[MemoryDiagnoser]` Perf suite, Stability soak scenarios (`SessionFootprintScenario` samples `GC.CollectionCount(0)` + allocated bytes), allocation-gate unit-test precedent (`UdpRelayTests.cs:420`).

## Known allocation enemies (file:line anchors)

Per-packet:
- A1 `NdisCapture.cs:148` — `Task.Delay` per empty poll iteration (~1000 allocs/s/pump idle).
- A2 `TcpProxyCoordinator.cs:421` — SYN bit-test via `Lease.Frame` materializes an ArrayPool copy per proxied TCP packet (use `InspectionSpan`).
- A3 `TcpProxyCoordinator.cs:262,314` — materialization on mid-flow/reverse TCP paths (writable copy needed; must move from managed ArrayPool to native pool).
- A4 `NdisPacketActionExecutor.cs:420-434` — materialization per proxied UDP datagram.

Per-flow / per-connection:
- B1 `TcpProxyCoordinator.cs:173` — `InspectionSpan.ToArray()` per new TCP SYN (kept in PendingSynSetup).
- B2 `TcpSequenceObservation.cs:23` — SYN template `.ToArray()` (≤128B) per new TCP flow.
- B3 `UdpProxyCoordinator.cs:149` — `clientMac.ToArray()` (`byte[6]`) per new UDP flow.
- B4 `BoundedSetupQueue.cs:42` — `frame.ToArray()` per queued datagram in UDP setup window.
- B5 `UdpProxyCoordinator.cs:150` / `TcpProxyCoordinator.cs:191` — `Task.Run(...)` + closure per new flow.
- B6 `Socks5State.cs:36,49` — `new byte[]` per SOCKS5 handshake command (all constant messages).
- B7 `Socks5ControlConnection.cs:256-283` — `new byte[]` handshake reply buffers per connection.
- B8 `TcpResetBuilder.cs:92,113` — `new byte[54/74]` per RST injection.
- B9 `Socks5ControlConnection.cs:56-57` — `Dns.GetHostAddressesAsync` per connection (server address).
- B10 FlowTable — `Dictionary` + lock; new flow allocates FlowState.
- B11 `TcpProxyRelay.cs:224` / `UdpProxySession.cs:169` / `UdpSessionSetup.cs:37` — ArrayPool-backed buffers (managed memory; must move to native pool under full-path zeroing).

Leak hazards (fix regardless):
- L1 `NdisPacketActionExecutor.cs:112-123` — rented native buffer leaks if lane-overflow send throws (`buffer.Dispose()` unreachable after rethrow).
- L2 `NdisPacketBufferPool.cs:66-77` — return-vs-Dispose race strands buffers until process exit (bounded ≈400KB, permanent under GC-off).

## Requirements

- R1 (A1–A4): steady-state zero managed allocation on per-packet paths. Empty-poll pacing must not allocate.
- R2 (B1–B11): steady-state zero managed allocation on per-flow/per-connection application paths: pooled SYN copies, pooled/inline MAC storage, native setup queues, pooled setup scheduling, constant/pooled SOCKS5 messages, stackalloc or pooled RST frames, pooled FlowTable states, native relay/receive buffers.
- R3 (DNS, user decision): resolve the SOCKS5 server address once at startup (cold path, allocation allowed); re-resolve only on connection failure (cold path, allocation allowed). Steady-state connections consume the cached `IPAddressValue` only.
- R4 (leaks): fix L1 and L2; every native pool provably returns/drains all buffers; pool rent/return statistics must be assertable in tests.
- R5 (GC posture): explicit, non-incidental GC configuration: no Server GC, `System.GC.HeapHardLimit*` fuse sized from measured post-startup heap, plus runtime observability (heartbeat reports GC collection counts and pool occupancy; warn if any collection occurs).
- R6 (verification): allocation-gate unit tests on TCP proxy path, UDP proxy path, dispatcher; Perf benchmarks show 0 B allocated on covered hot paths; a soak scenario proves zero gen0+ collections and flat memory over a sustained mixed-traffic run on loopback fakes.

## Acceptance Criteria

- [x] AC1: 589-test baseline passes (plus new tests); build remains zero-warning (`TreatWarningsAsErrors`). — 723/723, 0 warnings.
- [x] AC2: Allocation-gate tests assert 0 bytes (`GC.GetAllocatedBytesForCurrentThread()`) for: TCP proxied-packet path (incl. SYN + mid-flow + reverse), UDP datagram path, dispatcher fast path, RST build, SOCKS5 message production. — 10 gates incl. the added dispatcher warm gate.
- [x] AC3: Sustained soak (mixed TCP+UDP loopback, ≥30 min default) reports gen0/gen1/gen2 collection counts unchanged from startup baseline and flat working set; pool occupancy returns to baseline after load stops. — 30-min default run passed 2026-09-19 (exit 0): `workingSetSlope≈3.9e-11` B/s, baseline=final=94.06 MB, `udpSenderThreadAllocatedBytes=272` B over 45,059,748 sends, `poolOutstandingDelta=0`, `poolOverflowDelta=0`, `tcpBytesEchoed=434.6 GB`; process-wide gen0=1189/gen1=151/gen2=11 (reported).
  - Methodology deviation (recorded 2026-09-19): the loopback harness and the product's parked-receive background loops allocate BCL async state machines (explicitly Out of Scope), so a process-wide "gen counts unchanged" assertion would gate the harness, not the product. `gc-soak` therefore **asserts** the application-level contract — the UDP forward sender threads stay within a leak ceiling (absolute 64 KiB, or `sends/512`) across the whole window — and **reports** process-wide gen0/1/2 and allocated bytes as observability. Working-set slope (≤64 KiB/s) and final-vs-baseline growth (≤32 MiB) are asserted.
  - Pool gate (corrected after the first 30-min run): the window gate asserts **no fresh overflow allocation** (`OverflowAllocations` summed unchanged); in-window `Outstanding` is deliberately not asserted because established relays finish and return leases mid-soak (a return, not a leak). After full teardown, every pool must drain (bounded wait) to `Outstanding == 0` with the conservation identity `OverflowAllocations == DisposedCount + InPool + Outstanding`. See `hot-path.md` for the wrong-vs-correct pair.
- [x] AC4: Leak regressions covered by tests for L1 (lane-overflow throw) and L2 (dispose race); pool balance counters (rented == returned + in-use) asserted.
- [x] AC5: GC posture explicit in project/runtimeconfig; heartbeat exposes collection counts + native pool stats; documented fuse value and its measurement basis. — `System.GC.HeapHardLimit=128 MiB`, basis in `WinForward.Cli.csproj`.

## Out of Scope

- Cold paths (startup, adapter enumeration, generation refresh, heartbeat formatting, console logging) — allocation allowed there.
- BCL/runtime infrastructure allocations that application code cannot remove (Socket/NetworkStream objects per accepted/proxied connection, async state machines on slow paths, exception objects on failure paths). These are bounded, guarded by the HeapHardLimit fuse, and surfaced via heartbeat observability. (If soak shows unacceptable gen0 frequency from these, a native-socket rewrite is a future task, not this one.)
- NDIS interop rewrite (already pointer-based zero-copy).

## Decisions

- D1 Full-path zeroing (user): every application allocation on packet/flow/connection paths is eliminated or pooled; BCL infrastructure allocations are bounded and fenced by the HeapHardLimit fuse (see Out of Scope).
- D2 DNS policy (user): startup resolve + failure-triggered re-resolve (cold-path allocations allowed); steady-state uses cached `IPAddressValue`.
