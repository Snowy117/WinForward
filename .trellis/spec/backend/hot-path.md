# Hot-Path Conventions (Zero-Allocation Packet Pipeline)

> Established 2026-08-28 by task 08-28-perf-hotspots. Verified by the WinForward.Benchmarks
> harness (allocation/GC counters exact; ns/pps on the dev box carry ±50% noise).

## Scope / Trigger

Any code on the per-packet path: capture processing, parsing, classification, dispatch,
pass/block execution, flow-table probes, SOCKS5 UDP encode. Cold edges (config, process
attribution, socket setup, logging, tests) are exempt.

## Contracts

1. **Raw addresses only on the hot path.** `IPAddress` (class) never appears in
   parse/classify/flow-key code; use `IPAddressValue` (UInt128 bits, IPv4 in the low 32 bits,
   family + scope). Convert via `ToIPAddress()` / `From(IPAddress)` on cold edges only.
   `Endpoint` stores `IPAddressValue` by value.
2. **IPv4 masks stay inside the low 32 bits.** `IPPrefix.PrefixMask(prefixLength, family)`:
   IPv4 = `0xFFFFFFFF << (32 - len)` (/0→0, /32→0xFFFFFFFF); IPv6 = left-aligned 128-bit.
   A left-aligned mask over low-32 IPv4 bits matches everything — regression-locked by
   `IPv4PrefixesMatchOnlyTheirPrefix`.
3. **No async state machines on the steady-state path.** A fat async method (large struct
   locals hoisted into the state machine) heap-allocates per call even when it completes
   synchronously (~193 B/op measured). `FlowDispatcher.DispatchAsync` is a non-async entry
   that runs the synchronous warm shape (trace-off ∧ no reverse handler ∧ not self-owned ∧
   resolved ∧ Pass/Block) and returns the executor's ValueTask directly; everything else
   falls into `DispatchSlowAsync`. Any new per-packet stage must follow the same split.
4. **Packets are structs.** `FlowContext`, `CapturedFlowPacket`, `NativeFrameHandle` are
   `readonly record struct` with `[StructLayout(LayoutKind.Auto)]`; completion is
   enum-driven (`PacketAction`), never closures/delegates.
5. **Native-frame lease lifetime.** `PacketLease.TakeNative(IFrameSource)` recycles
   per-thread. The native frame stays valid for the whole dispatch (the pump awaits each
   handler, so batch slots cannot be reused earlier — locked by
   `PumpDoesNotReuseBatchSlotWhileHandlerIsInFlight`). Consumers that keep frame bytes past
   the synchronous section must read `Lease.Frame` (lazy ArrayPool materialization;
   `Release()` in the processor's finally returns it). Parse/classify run directly on the
   native span.
6. **In-place pass reinjection.** An unmodified pass frame reinjects its own capture buffer:
   `PrepareForReinjection(enumerationHandle)` retargets only the adapter handle (+ zeroes
   UnionPadding); DeviceFlags/Flags/Length/payload stay as captured. Materialized or
   buffer-less packets take the pooled-copy fallback. Enumeration handle ≠ captured
   `m_hAdapter` (see windows-ndisapi.md).
7. **Span-writing codecs.** Datagram encode uses `TryEncode(..., Span<byte>, out written)`
   into a reusable buffer (`Socks5UdpTransport._sendBuffer`); allocating overloads exist for
   tests only.
8. **Flow keys hash flat.** `FlowKey`/`TransportTuple` use manual `HashCode.Combine` over
   both endpoints' bits/ports/family/proto; `Equals` orders cheapest discriminators first
   and compares every field (including `OriginAdapterId` and `ScopeId`).
9. **Measurement discipline.** Benchmark gates are stated in allocation bytes and GC counts;
   treat ns/pps deltas under ~2× as noise on the dev box.
