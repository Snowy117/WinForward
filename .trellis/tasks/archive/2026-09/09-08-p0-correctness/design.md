# P0 design — correctness fixes from the 2026-09-08 design review

Evidence base: `../09-08-design-review-remediation/research/03-runtime-root-capture.md`, `02-ndisapi-windows.md`,
`00-synthesis.md` (P0 table). All line references verified against the tree on 2026-09-08.

## D1 — Executor pass-lane lifecycle (R1, highest priority)

### Problem recap

`NdisPacketActionExecutor._pendingLanes` keys pass-batching lanes by `(adapterHandle, direction)`
and never removes them (class comment + `TryGetOrAddPendingLane` doc, NdisPacketActionExecutor.cs:103-130).
The executor is durable across generations (`DurableCaptureBundle.CreateAsync`, DurableCaptureBundle.cs:96)
while `LayeredCaptureRunner.ProcessRefreshDemandAsync` rebuilds adapter handles on every real diff
(StopGenerationAsync → InstallGenerationAsync, LayeredCaptureRunner.cs:188-199). Fixed
`PendingLaneCapacity = 8` → after ~4 refreshes every slot holds a dead lane and all passes
permanently take the immediate single-send branch (AppendPass, :82-91) — silently.

### Chosen design: retire-by-scope notification

Add to `NdisPacketActionExecutor`:

```
internal void RetireLanesExcept(ReadOnlySpan<nint> activeAdapterHandles)
```

- Takes the creation lock (`_pendingLaneLock`), and for every live lane whose `AdapterHandle` is not
  in `activeAdapterHandles`: defensively drains it (returns rented buffers exactly once — post-flush
  lanes have `Count == 0`, so a non-zero count gets a rate-limited warn and the drain), then nulls
  the slot (`Volatile.Write`, mirroring publication).
- An empty span retires **all** lanes — the empty-scope case (interception paused) matches
  `UpdateUdpTargets`'s clear semantics.
- Documented precondition: callers must invoke this only when no pump for a retired handle can
  still be running (between generations). The runner's refresh pipeline guarantees exactly that:
  `StopGenerationAsync` awaits the old generation's run task and its cleanup (pump loop-exit flush
  included) before `InstallGenerationAsync`/`onScopeInstalled` fires.
- Driver handle-value reuse is safe: a numerically reused handle is indistinguishable from — and
  behaviorally equivalent to — the old lane key.

Wiring: `DurableCaptureBundle` gains `internal void OnScopeInstalled(IReadOnlyList<AdapterEnumerationItem> scope)`
that calls `UpdateUdpTargets(scope)` and then `Executor.RetireLanesExcept(<scope handles>)`;
Program.cs's `onScopeInstalled` callback delegates to it. Rationale: keeps Program.cs thin, keeps
`UpdateUdpTargets` independently testable, gives the retire step one discoverable home.

### Observability (part of the fix)

- In `AppendPass`'s lane-null branch: rate-limited `Warn` via the existing `ShouldWarn` pattern
  (new `_lastLaneOverflowLogTicks`), stating that passes degrade to immediate single sends because
  more than `PendingLaneCapacity` concurrent (adapter, direction) lanes are active. One branch
  covers both the >4-NIC overflow and the (now impossible) leak-exhaustion case.
- `internal long ImmediateSendLaneOverflowCount` (Interlocked) for deterministic test assertions,
  mirroring `MultiAdapterCaptureLoop.DegradedAdapterCount`.

### Rejected alternatives

- **Rebuild the executor per generation** — contradicts the durable-layer design (09-07-adapter-list-refresh
  §2), loses nothing but batching warmth and re-opens coordinator wiring.
- **Clear lanes inside `ICaptureGeneration` teardown** — couples the generation abstraction to
  executor internals; the scope-installed seam already exists for exactly this class of notification.
- **LRU lane eviction** — under handle churn it would evict *live* adapters' lanes, converting the
  silent degradation into a correctness bug (live lanes flushed early / split batches).

### Tests

1. Executor-level: Nth-lane overflow asserts `ImmediateSendLaneOverflowCount` + immediate send via
   `FakeReinjector` (batch API absent); `RetireLanesExcept` frees slots (9th lane creatable after
   retire); empty span retires all; defensive drain of a non-empty retired lane returns rented
   buffers exactly once.
2. Refresh-churn regression (the actual leak): runner harness with fakes, ≥5 generations × 2
   adapters = 10 distinct handles; assert the final generation still batches (FakeReinjector batch
   call observed, zero overflow count). Uses the ordered-event seam for stop→retire→install
   ordering assertions where the harness supports it.

## D2 — `IPAddressValue.IPv4Any` (R2)

- Add `public static readonly IPAddressValue IPv4Any = new(0, AddressFamilyKind.IPv4);` to
  `IPAddressValue` (near `FromIPv4`; the ctor validator accepts 0). Self-documenting constant
  beats inline `FromIPv4(0u)` at the call site and matches `IsIPv4Any` semantics.
- `PacketFlowClassifier.ClassifyNonFlow` line 40 → `Endpoint.From(IPAddressValue.IPv4Any, 0)`
  (the `IPAddressValue` overload exists, Domain.cs:58); drop `using System.Net;` from the file.
- Verification: `rg "IPAddress\b" src/WinForward.Runtime/PacketFlowClassifier.cs` comes back clean;
  warm-path allocation gates unaffected (struct constant replaces a class-conversion call — strictly
  less work). Existing classifier/parsing tests pin the 0.0.0.0:0 degenerate endpoints.

## D3 — `IPHelperTables.ReadTable` bounds cross-check (R3)

- `ReadTable` (ProcessAttribution.cs:258-276) gains an `out uint bytesWritten` carrying the
  post-success `size` value; each `Read*` caller validates before any row deref:
  `4 + (long)rowCount * sizeof(T) ≤ bytesWritten`.
- Validation extracted as `internal static void ValidateRowCount(int rowCount, uint bytesWritten, int rowSize, string tableName)`
  throwing `InvalidOperationException` with an actionable message (table, rowCount, bytesWritten,
  rowSize) — fail-closed. Attribution callers already tolerate attributor failure (retry/LRU/null
  identity path), so an inconsistent table becomes "no attribution", never a crash or a wrong PID.
- Rejected: clamping rowCount to the fitting rows (fail-open; a silently truncated owner table
  could mis-attribute policy decisions).
- Tests: pure unit tests over `ValidateRowCount` (exact fit, off-by-one-byte overflow, zero rows,
  minimum 4-byte buffer), independent of P/Invoke.

## D4 — Pump `DisposeAsync` misuse-proofing (R4)

New contract for `NdisCapturePump`:

- `RunAsync` registers a completion source at entry and completes it in its `finally` (where
  buffers are already released, guarded by `_buffersReleased`).
- `DisposeAsync` = `Interlocked.Exchange(ref _stopped, 1)` **+ await the in-flight run's completion
  (if any) + release buffers (idempotent)**. Fast path when no run ever started.
- Liveness argument: the loop checks `_stopped` every iteration; the only waits are `Task.Delay`
  bounded by `pollDelay` (default 1 ms). A custom large `pollDelay` bounds the dispose wait; the
  class doc states this.
- `MultiAdapterCaptureLoop.DisposeAsync` needs no change — its sequential awaits now naturally wait
  for each pump's exit.
- Tests (NdisCapturePumpTests, TCS-gated fake reader): dispose mid-run completes only after the
  loop observes stop; buffers released exactly once on every path; dispose-after-completion is a
  synchronous fast path.

## D5 — Lease-null guard exception (R5)

- The misleading `if (packet.Lease is null) throw new ArgumentNullException(nameof(packet));`
  pattern exists at **9 sites** (rg-verified: FlowDispatcher.cs:128,163,254;
  TcpProxyCoordinator.cs:102,296,379,408,458; NdisPacketActionExecutor.cs:55) — `packet` is a
  struct and never null; the null member is the lease.
- Uniform replacement: `throw new ArgumentNullException("packet.Lease");` (parameter name renders
  correctly in stack traces; minimal diff). One executor test asserts `ParamName` — the other 8
  sites follow by construction.
- Scope note: PRD listed only the executor site; extending to all identical sites is the same
  mechanical change and avoids leaving landmines the review already flagged once.

## Compatibility & risks

- Public surface grows only by `IPAddressValue.IPv4Any`; everything else is `internal` or
  behavior-on-error-path. No wire format, ABI, or config changes.
- R1/R4 touch cold paths (generation switch, disposal); R2 strictly reduces per-classify work.
- All concurrency tests use TCS/barrier gating per quality-guidelines.md:20 — no scheduler timing.

## Rollout / rollback

Six independently revertable commit batches (see implement.md). Any batch failing its validation
gate is `git revert`ed in isolation; batches have no forward dependencies except batch 5's wiring
depending on the D1 API from batch 5 itself (single batch).
