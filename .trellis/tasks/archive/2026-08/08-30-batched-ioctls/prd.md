# Batched reinjection IOCTLs (backlog #7 / research X3)

Parent: `08-30-proxy-perf-stability`. Elevated priority by the 08-30 Windows VM data:
managed CPU cost is platform-equivalent, but per-IO cost is the structural Windows gap
(TcpRelay 12.4× at chunk=1 → 2.9× at 8192 proves batching amortizes exactly this).

## Problem

Every reinjection today is one kernel crossing. A pump batch of 32 same-direction Pass
packets = 32 `DeviceIoControl` calls + 32 adapter-gate lock acquisitions. The capture
(read) side is already batched (`ETH_M_REQUEST`); the write side is not. The batched ABI
(`SendPacketsToAdapter`/`SendPacketsToMstcp`, `NdisApiAbi.cs:217-227`) is imported but
unused.

## Injection-path scope (from the 08-30 traffic map)

| consumer | path | temperature | in scope |
|---|---|---|---|
| `NdisPacketActionExecutor.PassAsync` (:51,:58) | pump thread, per packet | hot | **Phase 1** |
| `UdpResponseReinjector` (:123,:127) | IOCP thread, per UDP response | hot under DNS load | Phase 2, conditional |
| `TcpRedirectInjector` (:21) SYN diversion / RST | per flow / error path | cold | out |
| TCP mid-flow relay | userland sockets, no injection | — | out (mux line) |

## Requirements

### R1 — Phase 1: Pass batching in the executor

- Accumulate Pass dispositions per (adapterHandle, direction) within a single pump
  iteration; flush once at iteration end (before the next `ReadPackets`) and on loop
  exit (teardown never drops pending frames). Cross-key ordering is not a contract —
  flows never mix dispositions, so per-flow order is untouched.
- Safety rests on the pump's serialized batch loop (strictly ordered awaits), not on
  thread identity — the loop may resume on different thread-pool threads. All current
  `PassAsync` callers live inside that chain; a call-site audit plus a debug assert
  (flush every iteration) pins the invariant.
- Slot stability: batched frames stay in place (in-place capture buffers for
  unmaterialized packets, pooled buffers otherwise); the batch-slot contract is
  unchanged because slots are stable for the whole iteration today.
- Batch failure semantics (revised after S2 export verification): the send IOCTLs pass
  `lpOutBuffer=NULL` with `METHOD_BUFFERED`, so `PacketsSuccess` is unobservable on the
  send path — the API is all-or-nothing. A failed batch throws the same
  `Win32Exception` (native error, direction, adapter, counts) as today's single-packet
  failure; there is no suffix-resend path. Evidence:
  `research/abi-packets-success.md`.

### R2 — Adapter-gate lookup without per-call lock

- `NdisAdapterGateMap.Get` takes a `Lock` on every native call today
  (`NdisNativeCallGate.cs:70-82`). Replace with an enumeration-time frozen snapshot or
  lock-free read path; updates (if any exist at runtime) remain correct.

### R3 — Phase 2 (conditional, separate decision): UDP response micro-batching

- Only merge responses that arrive within the same IOCP drain; never wait. Requires a
  cross-thread accumulator with an immediate flush. Do not start until Phase 1 lands and
  a DNS-dense measurement justifies it.

## Acceptance Criteria

1. Full test suite green, zero-warning build (baseline 463 tests).
2. New unit tests pin: same-key accumulation ordering; flush at iteration end and on
   loop exit; batch-failure propagation (fail-the-batch, per S2); lane-overflow
   single-send fallback; exactly-once pool return for both in-place and pooled frames;
   counting-reinjector ≥10× call reduction with per-flow order preserved.
3. Allocation gates in `CapturePumpBenchmarks`/`DispatcherBenchmarks` do not regress
   (in-place Pass remains copy-free; batching adds no per-packet allocation).
4. Ordering contract documented in spec: same (adapter, direction) Pass packets are
   sent in capture order; interleaving with different keys or proxy injections preserves
   today's behavior.
5. Syscall-count evidence: an info-level metric (per-flush batch size histogram or
   counter) shows ≥10× reduction in reinjection calls per pump iteration under a
   mixed pass workload (verifiable in a unit/bench harness with a counting reinjector;
   real-NIC validation stays with `windows-real-nic`).

## Out of Scope

- Partial-success retry/backoff policy (belongs to #10 driver-resilience).
- Zero-copy work beyond what already landed with #1 (in-place pass reinjection stays).
- TCP relay data path, SYN/RST injection batching, NDIS real-path benchmarking.
- `windows-reality` loopback ceiling (measurement property, not this task).
