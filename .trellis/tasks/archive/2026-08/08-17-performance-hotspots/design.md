# Technical Design: Performance Hotspots

## 1. Scope And Decisions

This task is a single, ordered optimization effort rather than a parent with child tasks. The benchmark and capture-path changes are prerequisites for interpreting the later table, session, relay, and logging changes; splitting them would make each child independently unverifiable without duplicating the same baseline and Windows smoke setup.

The primary workload is low-latency host pass and short-lived TCP connections. High-concurrency UDP memory and sustained TCP relay throughput are secondary workloads. Every optimization is measurement-gated: a change that improves one metric while regressing pass p99, loss, direction, lifecycle safety, or fail-closed behavior is rejected or rolled back.

The design preserves the existing pinned WinpkFilter/NDISAPI contract. `GetTcpipBoundAdaptersInfo` enumeration handles remain the only request handles, `DeviceFlags` and NDIS metadata flags remain distinct, and the one-driver native call serialization contract remains unless a hardware-verified replacement preserves the same ABI safety.

## 2. Current And Target Data Flow

Current common path:

```text
NDIS queue probe -> single ReadPacket -> new unmanaged buffer per poll
  -> managed frame ToArray -> parse/classify -> locked flow/self checks
  -> policy/attribution -> action
  -> new unmanaged buffer + full-frame copy -> native reinjection
```

Target pass path, after measurement-gated rollout:

```text
event or bounded wait -> drain one/batched capture read using reusable native storage
  -> one owned frame representation -> allocation-conscious classify/lookup
  -> pass/block or pooled/reused reinjection storage -> native reinjection
```

Target proxied paths retain explicit ownership boundaries but remove redundant representation changes:

```text
captured frame -> one parser/view with offsets -> TCP rewrite or UDP payload view
  -> pooled/bounded SOCKS5 transport buffer -> reinjection/build only when required
```

The capture handler must still not retain native memory beyond the pump iteration. Any zero-copy or pooled view must carry an explicit owner lifetime and be invalidated/returned only after the final async consumer completes.

## 3. Optimization Boundaries

### 3.1 Baseline And Instrumentation

Add a release-mode performance harness for pure protocol, flow-table, dispatcher, and relay primitives. It records warmup, operation count, elapsed time, allocated bytes, allocation count where available, and copied-byte counters. Add a Windows-only runbook or script for NDISAPI/ETW measurements; the harness must not infer driver latency from Linux results.

The baseline matrix covers 64, 512, and 1514 byte frames; pass, block, TCP rewrite, UDP encode/decode, cold/warm proxy setup, flow-table cardinalities, UDP session cardinalities, TCP chunk sizes, and logging levels. The recorded historical 8 ms versus 0.6 ms values remain a comparison point, not a new acceptance result.

### 3.2 Capture And Native ABI

First verify the exact exports and structures for the pinned NDISAPI version. If available and hardware-verified, replace empty-queue 1 ms polling with packet-event waiting and bounded batch draining. Reuse per-pump native packet storage where the driver contract permits it. Keep the native call gate around all mutable-driver operations and document any shorter lock scope.

The implementation must preserve cancellation, adapter-specific enumeration handles, packet metadata, buffer bounds, and sibling-pump failure propagation. A single-read/event-wait fallback is retained only if the pinned ABI requires it or the event/batch path fails verification; the fallback must be benchmarked separately.

### 3.3 Packet Representation And Copies

Use offset/length packet views for UDP payloads so parsing does not eagerly materialize a payload array. Thread the captured managed frame owner through the action path and use a bounded native-buffer reuse mechanism for reinjection where concurrent background TCP/UDP responses require independent ownership.

Measure before changing address representation. If `IPAddress` construction is material, introduce a value-type address/key representation at the parser-to-flow boundary and materialize `IPAddress` only at APIs that require it. Do not weaken malformed-frame, fragment, address-family, extension-header, checksum, or maximum-frame checks.

TCP rewrite remains allocation-free over a mutable span after the frame owner is acquired. UDP response construction remains a new frame when required, but its payload and native handoff should avoid an unnecessary intermediate copy and must continue to enforce the pinned Ethernet frame cap.

### 3.4 Flow, Policy, And Self-Traffic Lookups

Add a canonical transport-tuple index to `FlowTable` so reverse, origin-flipped, and cross-adapter observations are O(1) without the adapter-agnostic value scan. Keep `TryResolve` as the observing API and preserve `TryGet` as non-observing if callers depend on that distinction. The existing dispatcher double call may remain as two O(1) lookups; it must no longer cause two O(n) scans.

Precompute whether the policy contains process selectors and skip Windows owner-table attribution when it cannot affect any decision. Preserve host-only attribution and the existing cache identity semantics when attribution is required.

Index self-traffic wildcard candidates by protocol/remote/port while preserving exact and reverse matching. Keep the single-gate ownership contract unless measurements show a safe narrower lock; no lookup may observe a token after disposal.

### 3.5 UDP Sessions And TCP Relay

Bound UDP receive storage to the maximum reinjectable datagram size derived from the configured/pinned Ethernet frame cap, and use `ArrayPool<byte>` or an equivalent ownership-aware pool. A session returns its rented storage on every disposal path, including setup failure and coordinator shutdown. Oversized or truncated datagrams remain fail-closed.

Move TCP relay timeout setup from per-read/per-write linked CTS creation to one reusable timeout lifecycle per direction or an equivalent socket-level mechanism. Preserve the 30-minute no-progress behavior, external cancellation, half-close semantics, sibling cancellation, and exception observation. Benchmark small and large application write sizes because the allocation benefit is expected to be throughput-sensitive.

### 3.6 Logging

Guard high-frequency event construction before creating `params` arrays or boxed field values. Keep the existing event names, field order, privacy rules, threshold semantics, synchronous writer behavior, and logger-failure isolation. Logging remains observational and cannot participate in packet disposition or shutdown decisions.

## 4. Compatibility And Rollback

- No configuration, policy, protocol, adapter identity, or user-visible log contract changes are planned.
- Native ABI changes are isolated in `WinForward.NdisApi`; they require layout/export verification and a Windows smoke test before activation.
- Each optimization batch is independently revertible: capture/ABI, packet representation, tables/attribution, session/relay, and logging.
- If a Windows path cannot be verified, keep the existing implementation as the measured baseline and do not claim end-to-end improvement.
- Rollback criteria are packet loss/duplication, wrong reinjection direction, leaked native/pool resources, changed fail-closed behavior, p99 regression on the primary workload, or analyzer/test failures.

## 5. Verification Contract

The final report must include baseline and candidate results for the primary matrix, secondary memory/throughput matrices, test/build output, and the Windows hardware limitations of any result not reproduced locally. Correctness tests must cover buffer lifetime, pool return, table observation/touch, concurrent claims, timeout cancellation, NDIS direction/handle/metadata, and existing packet rewrite invariants.
