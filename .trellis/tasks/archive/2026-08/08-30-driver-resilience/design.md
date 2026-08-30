# Design — Driver resilience (R7 + R8)

All file:line references are against post-batched-ioctls master (f9fdb67 + journal commits).

## R7 — Transient driver-error retry + single-pump degradation

### Failure path today

`NdisCapturePump.RunAsync` (`NdisCapture.cs:69-104`) calls `INdisPacketReader.TryReadPackets`
synchronously per iteration. Every failure surfaces as `Win32Exception` from
`NdisNativeCallStatus` (`NdisNativeCallStatus.cs:22-45`: `HasQueuedPackets`,
`InterpretBatchReadResult`). The exception exits the pump → `MultiAdapterCaptureLoop.RunPumpAsync`
catches, cancels sibling pumps, rethrows (`MultiAdapterCaptureLoop.cs:58-73`) →
`TransactionalCaptureRuntime.StartCoreAsync` finally-cleanup runs → `Program.cs:223-226` catches,
process exits with code 3. Net effect: any single-adapter disturbance (removal, power transition,
driver pause) stops interception on all adapters.

### Error classification (extend `NdisNativeCallStatus`)

New internal classifier `IsTransientReadError(int nativeError)`:

| Class | Win32 codes | Pump behavior |
|---|---|---|
| Transient | `ERROR_NOT_READY` (21), `ERROR_BUSY` (170), `ERROR_RETRY` (1237), `ERROR_OPERATION_ABORTED` (995), `ERROR_DEVICE_NOT_CONNECTED` (1167), `ERROR_GEN_FAILURE` (31) | backoff-retry |
| Permanent | everything else (incl. `ERROR_INVALID_HANDLE` 6, `ERROR_FILE_NOT_FOUND` 2, `ERROR_INVALID_PARAMETER` 87) | degrade immediately |

Unknown/unclassified codes fall on the **permanent** side (conservative fail-closed), matching
the project's fail-closed default. Rationale: a mis-classified permanent error only costs one
bounded retry window (~3 s) before degradation anyway; mis-classified transient codes would
silently keep today's global-exit behavior.

**S1 evidence findings (2026-08-30, recorded)**: the ndisrd driver itself is closed-source
(WinpkFilter is a commercial NT Kernel product; the wiresock/ndisapi repo ships only the
user-mode library), so no authoritative in-driver status table exists. Adjacent evidence:
Npcap (same NDIS filter-driver class, nmap/nmap#2036) reports adapter-removal / sleep-wake
transitions surfacing as `ERROR_OPERATION_ABORTED` (995) — `STATUS_CANCELLED`-mapped — and
newer builds map device removal to `STATUS_DEVICE_REMOVED`, whose userland projection is
`ERROR_GEN_FAILURE`/`ERROR_DEVICE_NOT_CONNECTED` class. This **supports** the table above
(no conflict with the S1 gate): 995 is confirmed transient-class, and the two removal
projections are covered without extending the retry window risk (both stay retryable then
degrade). Classification stays conservative for everything else. Implementation detail that
feeds back into the table: the degradation log MUST record the full native error code (already
required by the telemetry design), so a real Windows run (S8 VM spot-check or production)
provides ground truth to refine the table later.

### Retry loop (inside `NdisCapturePump.RunAsync`)

- Constants: `TransientRetryMaxAttempts = 5`, `TransientRetryBaseDelay = 100 ms`, doubling per
  attempt, per-attempt cap 1.6 s → worst-case window ≈ 3.1 s.
- On a read failure whose native error classifies transient: increment retry counter, emit a
  rate-limited warn (`adapter.retry`, native error, attempt N of 5), `Task.Delay(backoff,
  cancellationToken)`, continue the loop. `_onBatchCompleted` contract unchanged (flush still
  runs every iteration boundary).
- Retries exhausted, or permanent error → **degraded exit**: the pump breaks its loop and returns
  normally (no throw). Batch-buffer release and final flush callbacks run through the existing
  `finally` (`NdisCapture.cs:97-103`).
- Injection-path (`SendPackets*`) failures are out of scope: those already fail-closed per flow
  inside the handlers, not per process.

### Degradation plumbing (loop → runtime)

1. `NdisCapturePump` gains a completion status (e.g. `RunOutcome` / a `Degraded` flag readable
   after `RunAsync` completes, or an `onDegraded` callback invoked at degraded exit carrying the
   native error). Callback shape preferred: constructor-optional `Action<int>? onDegraded`,
   mirroring the existing optional `onBatchCompleted`.
2. `MultiAdapterCaptureLoop`: a degraded pump completes normally — `Task.WhenAll` keeps waiting
   for siblings, sibling cancellation is NOT triggered (the current rethrow path stays for
   non-driver exceptions). The loop forwards the degradation event to a new constructor parameter
   `Func<WindowsAdapter, int, ValueTask>? onAdapterDegraded`. Zero degraded adapters keeps the
   loop's observable behavior identical to today.
3. `TransactionalCaptureRuntime` gains `MarkAdapterDegradedAsync(string adapterId)`:
   under `_gate`, find the snapshot in `_applied` (matching `AdapterId`), remove it, then outside
   the lock `await _modes.RestoreAsync(snapshot, CancellationToken.None)` best-effort (restore
   failure is logged, never thrown). Removing from `_applied` prevents the normal-shutdown path
   from restoring it twice; a double restore would also be harmless (mode flags are idempotent)
   but the removal keeps the bookkeeping honest.
4. Wiring in `Program.RunCaptureLoopAsync`: pass
   `onAdapterDegraded: (adapter, error) => { logger.Error(...); return runtime.MarkAdapterDegradedAsync(adapter.StableId); }`.
   The existing top-level catch (`Program.cs:223-226`) remains for every other exception source.

### Semantics after degradation

- The degraded adapter's interception stops: its flows' reinjections fail per-flow (existing
  fail-closed handling inside coordinators), sessions idle-expire via the existing sweep.
- The process stays up: sibling adapters keep proxying; Ctrl+C still performs the normal
  shutdown+restore for the remaining adapters.
- If every adapter ends up degraded the runtime keeps running with zero pumps — acceptable
  (matches "resilient host" goal; log makes it observable). No auto re-enumeration (that is the
  deferred `SetAdapterListChangeEvent` seam, `Program.cs:169-172`, explicitly out of scope).

### Telemetry

- Per-pump Interlocked counters: `TransientReadRetryCount`, `DegradedAdapterCount` (exposed via
  internal properties on `NdisCapturePump` / `MultiAdapterCaptureLoop`, mirroring the batching
  telemetry pattern in `NdisApiDriver`), plus rate-limited warn/error logs (`adapter.retry`,
  `adapter.degraded`).

## R8 — TCP SYN setup off the pump thread

### Problem shape

`HandleSynAsync` (`TcpProxyCoordinator.cs:95-155`) runs on the pump's strictly-ordered handler
chain and holds `_store.EnterSetup()` while `TcpRedirectSetup.SetupNewRedirectAsync`
(`TcpRedirectSetup.cs:55-108`) awaits `TcpRedirectListenerFactory.CreateAsync`
(`TcpRedirectListener.cs:11-34`) — which is synchronous `Bind`/`Listen` on the calling (pump)
thread. Port-exhaustion / slow-bind therefore head-of-line blocks every later packet on that
adapter.

UDP already solved the same problem (spec `error-handling.md` §UDP-setup): non-async pump entry,
per-flow slot + `Task.Run` background setup (`UdpProxyCoordinator.cs:149-178`), bounded setup
queue, global concurrency cap, tombstone cooldown on genuine failure. This design ports that
contract to the TCP SYN path.

### Pump-side fast path (synchronous, no awaits added)

`HandleSynAsync` keeps, unchanged and synchronous: existing-association reuse
(`TryResolveByOriginal`), tombstone/TIME_WAIT grace hit, capacity gate + RST|ACK (S4). For a
genuinely new flow, replace the awaited `SetupNewRedirectAsync` call with:

1. Look up / create a **pending entry** in a new coordinator-owned pending index
   (`Dictionary<FlowKey, PendingSynSetup>` under its own small lock; NOT the store gate).
2. Store in the entry (first writer wins, later retransmissions **overwrite**): a materialized
   copy of the SYN frame + the packet metadata needed for rewriting/injection (origin adapter
   handle, sequence/generation). TCP SYNs carry no payload, so retaining only the newest copy is
   sufficient — simpler and tighter than UDP's 32-deep datagram queue.
3. If the entry was just created, launch the background setup:
   `Task.Run(() => SetupPendingAsync(entry, ...))` — no `SemaphoreSlim` throttle in v1 (bind is
   sub-millisecond; the pending-entry capacity bound below is the real guard). If a setup task is
   already running for the key, do nothing.
4. Return new outcome `TcpRedirectOutcome.SetupPending` (dispatcher treats it as a no-op: nothing
   injected now, nothing failed).

### Background setup (`SetupPendingAsync`)

Mirrors `SetupNewRedirectAsync`'s steps with ownership moved:

- `_store.EnterSetup()` / `try` / `finally ExitSetup()` wraps the background body (the existing
  inflight-setup drain counter, `TcpRedirectSessionStore.cs:68-83`, then covers background
  setups; dispose-drain semantics are preserved unchanged).
- Listener allocation (`CreateAsync`) runs here — off the pump thread by construction.
- Claim: `TryClaim` with the freshly bound `translatedTuple` (claim could not move earlier: the
  tuple does not exist before bind). Exactly-once is preserved by the existing table claim; a
  concurrent setup that loses the claim releases its redundant listener — the existing
  concurrent-loser path (`TcpRedirectSetup.cs:96-105`) already handles this, now with higher
  probability (retransmissions inside the pending window).
- On claim success: rewrite the **retained SYN copy** toward the listener and inject it
  (existing `CompleteNewRedirectAsync` tail: sequence observation, `TryRewriteForwardLeg`,
  `InjectAsync`, `RegisterSession`, accept-loop start — reused as-is, fed from the retained copy
  instead of the pump's native frame).
- On genuine setup failure (bind throws, injection fails): fail-closed for that flow — drop the
  pending entry, no listener leak, log warn. A 1 s per-flow cooldown tombstone (reuse the
  existing tombstone table shape) prevents retransmission-rate hammering, mirroring UDP's
  `udp.setup.cooldown`.

### Bounding the pending state

- Pending entry count cap: 1024 (distinct original keys); a SYN arriving at cap is
  fail-closed-dropped with trace (`tcp.setup.pending.dropped`), same posture as UDP capacity.
- Global retained-frame budget: 1 MiB Interlocked byte budget (a frame is ≤ ~1514 B, so the cap
  binds on entry count long before bytes; the byte budget exists for symmetry and future-proofing
  against jumbo capture frames), charged on first retain, credited exactly-once at every sink
  (setup completion, overwrite, TTL expiry).
- TTL: 5 s per retained SYN (covers the client's first two retransmissions); an entry whose
  background setup has not completed is expired-and-credited on the next coordinator touch or by
  the existing idle sweep extension point (design choice: lazy expiry on touch + sweep hook,
  same as UDP's TTL enforcement).

### Order-safety argument (spec reconciliation)

The spec's pump contract ("packets within a batch are awaited strictly in index order, so
reinjection order matches arrival order", `windows-ndisapi.md`) is scoped to per-flow
correctness: a flow's rewritten SYN must precede that flow's data toward the listener. TCP itself
enforces the precondition — the client sends data only after the handshake completes, which
requires the injected SYN-ACK, which requires our injected SYN. Within the pending window
(≈ bind + schedule latency, sub-millisecond nominal) the only same-flow packet that can arrive is
a **SYN retransmission**, which the pending index absorbs (overwrite, no second setup). The spec
clause gains a note: deferred SYN injection via the pending path intentionally relaxes
cross-flow injection ordering on one adapter; per-flow ordering is preserved by construction.

### Store-gate audit

- Pump side: `HandleSynAsync` no longer holds `EnterSetup` at all (its whole body becomes
  synchronous; the gate moves into the background task). `RetireSessionUnderGate` (D1) semantics
  are untouched: retire still excludes setups atomically — now including background ones — via
  the same inflight counter.
- Lock order: pending-index lock is leaf-level (never taken while holding the store gate or
  table gate). The background task takes store gate → table gate → tombstone gate exactly as the
  current setup path does.

## Risks / trade-offs

- **R7 mis-classification**: a permanent error classified transient delays degradation by one
  bounded window (~3 s) — acceptable. The ndisrd-source cross-check step mitigates.
- **R7 degraded-adapter residue**: sessions on a dead adapter linger until idle expiry; memory
  is bounded by existing capacities. No new unbounded state.
- **R8 SYN injection latency**: moves from "pump-wait" to "background" — nominal delta is
  scheduler latency (µs); worst case under port exhaustion is the same bind duration, now
  off-thread. Retransmission backstop unchanged.
- **R8 new outcome enum value**: additive; all existing switch sites audited in implementation
  (compiler exhaustiveness assists).

## Test strategy

R7: fake `INdisPacketReader` injecting classified errors — (a) transient-then-success continues
the run with retry counter incremented; (b) permanent error degrades without rethrow, siblings
uncancelled (fake multi-pump loop test); (c) exhaustion after N transients degrades; (d)
degradation callback restores the adapter mode snapshot (fake `IAdapterModeController`);
(e) rate-limited logging asserted via recording logger.

R8: (a) controllable slow `ITcpRedirectListenerFactory` + latency probe on the next packet's
handler completion — pump must not await the bind; (b) retransmitted SYN during pending absorbs
into one setup (one listener created — fake factory counts); (c) background completion injects
exactly one rewritten SYN (fake injector records); (d) loser path releases redundant listener;
(e) pending cap + byte budget + TTL expiry crediting exactly-once; (f) dispose during inflight
background setup drains cleanly (`_setupsDrained`); (g) capacity/tombstone fast paths unchanged
(existing suites stay green).
