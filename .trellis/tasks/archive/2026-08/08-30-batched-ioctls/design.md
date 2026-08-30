# Design — Batched reinjection IOCTLs (Phase 1)

## Verified facts (code-level)

- One `NdisCapturePump` per adapter (`MultiAdapterCaptureLoop.cs:25`); batch = single
  `TryReadPackets`, slots 0..readCount-1 awaited **strictly in order**
  (`NdisCapture.cs:77-84`) — reinjection order within an adapter matches arrival order
  is the existing contract. Physical thread may drift across awaits (all
  `ConfigureAwait(false)`), but access is logically serialized by the batch loop.
- `NdisPacketActionExecutor.PassAsync` (:44-60) already reinjects in place for
  unmaterialized packets (`PrepareForReinjection` + single send); pooled buffer only for
  rewritten frames. Batch slots are stable for the whole iteration, so deferring the
  send to iteration end does not change slot lifetime.
- The injection consumers in a batch loop are: Pass (executor), TCP redirect SYN/RST
  (`TcpRedirectInjector`, per-flow/cold, sends immediately), UDP proxy consume (no
  injection). A flow's disposition is flow-level — a single flow never mixes Pass and
  proxy injection, so reordering Pass sends across flows cannot reorder any single
  flow's packets.
- `NdisAdapterGateMap.Get` (`NdisNativeCallGate.cs:70-82`) locks a `Dictionary` per
  native call; the map only grows and is bounded by the adapter count.
- Batched ABI already imported (`NdisApiAbi.cs:217-227`); `EthernetMultiRequest` +
  `BuildMultiRequest` (`NdisApiDriver.cs:138-183`) build stack-resident requests with
  raw pointer slots; frames live in native memory — no pinning, no GC pressure.
- `UdpResponseReinjector` injects from IOCP threads, one IOCTL per response — shares
  the driver gates but never the batch accumulator (Phase 2 territory).

## Design

### D1 — Driver: batched send API (`NdisApiDriver`)

```
SendPacketsToMstcp(nint adapterHandle, NdisPacketBuffer[] buffers, int count)
SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers, int count)
```

- Build an `EthernetMultiRequest` via the existing `BuildMultiRequest` shape (count ≤
  batch capacity ≤ 125 keeps the 1KB stackalloc budget; assert & chunk above that).
- One gate lease per flush (per-adapter serialization contract preserved).
- Partial success: trust `PacketsSuccess` only after clamping (`NdisNativeCallStatus`
  discipline). On `PacketsSuccess < count`, resend the non-acknowledged suffix via the
  existing single-packet methods so today's fail-fast semantics apply to exactly the
  packets that failed; rate-limited warn carries both counts.
- **Export-verify the `PacketsSuccess` prefix/suffix semantics against the real DLL
  before trusting the resend logic** (same discipline as the 08-27 ABI verification);
  if unverifiable, fall back to fail-the-batch (throw, same as today's single failure).

### D2 — Executor: batch accumulator (`NdisPacketActionExecutor`)

- State: per-direction two slot lists (`mstcp`, `adapter` — the adapter handle is
  constant per pump, but keys are (handle, direction) pairs kept in a tiny fixed array
  ≤4 entries, linear scan, zero allocation).
- `PassAsync` (called only from the batch loop — dispatcher paths are all inside the
  pump's awaited handler chain): append the prepared buffer to its key's list instead
  of sending. Unmaterialized packets keep the in-place buffer; materialized ones keep
  the rented pooled buffer (returned after flush, preserving exactly-once pool return).
- `FlushPendingPasses()`: for each key in insertion order, call the D1 batched API with
  the accumulated list, return rented buffers, clear. Empty flush is a no-op.
- Key change (a Pass of a different (adapter, direction) than the last appended) does
  **not** force a flush: lists are per-key, and cross-key order is not a contract
  (different directions/adapters commute; flows never mix). Flush happens once at
  iteration end. This maximizes merging (2 IOCTLs worst case per iteration).

### D3 — Batch-end wiring

- `NdisCapturePump` gains an optional `onBatchCompleted` callback invoked after the
  slot loop, before the next `TryReadPackets` (cancellation-safe; also invoked when the
  loop exits via `finally` so teardown never leaves pending frames).
- `MultiAdapterCaptureLoop` wires `pump → processor.OnBatchCompleted →
  executor.FlushPendingPasses()`. The processor already owns the executor reference
  chain; a thin pass-through keeps layering intact (no executor leak into the pump).
- Every non-batch caller of `IPacketReinjector` (TCP injector, UDP response
  reinjector) is unaffected: they talk to the driver directly and only share gates.

### D4 — Gate map without per-call lock

- Replace `Lock + Dictionary` with `ConcurrentDictionary.GetOrAdd` (map only grows;
  GetOrAdd may construct a discarded gate at most once per handle — harmless because
  gates are lazily-registered passive objects). `GetMaxConcurrentCalls` reads the
  concurrent enumeration. This removes the shared lock from every native call without
  changing semantics.

## Ordering & invariants (spec-worthy)

1. Same (adapter, direction) Pass frames are reinjected in capture order (append order
   == slot order).
2. Pass sends may shift relative to interleaved TCP redirect injections of *other*
   flows within one iteration; single flows never mix dispositions, so per-flow order
   is untouched.
3. Batch-slot stability: slots are stable for the whole iteration (unchanged).
4. Exactly-once pool return: rented buffers are returned at flush; in-place capture
   buffers are never returned (unchanged).
5. Teardown: flush-on-exit prevents frame loss on stop; OCE during flush propagates
   after the flush attempt.

## Observability

- Counting metric on the driver batched path: flush count and packets-per-flush
  histogram exposed via the existing telemetry surface (info-level periodic log or the
  gate telemetry snapshot pattern). A counting reinjector in tests/benchmarks asserts
  ≥10× call reduction under mixed pass load (AC5).

## Risks

- `PacketsSuccess` semantics mismatch → mitigated by export verification + fail-batch
  fallback (D1).
- Hidden `PassAsync` callers outside the batch loop → mitigated by call-site audit in
  implementation + a debug-mode assert that flush happens every iteration.
- Multi-adapter packets in one pump iteration cannot happen (pump is per-adapter), but
  the accumulator is keyed defensively anyway.
