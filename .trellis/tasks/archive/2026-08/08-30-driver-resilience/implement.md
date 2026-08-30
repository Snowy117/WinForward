# Implementation Plan — Driver resilience (R7 + R8)

Validation commands (run from repo root):

- Build: `dotnet build -c Release`
- Tests: `dotnet test -c Release` (baseline: 476/476; known pre-existing flaky:
  one `UdpProxyCoordinatorLifecycleTests` case — rerun-on-failure, not a regression signal)

Steps S1–S4 are R7 (independent), S5–S8 are R8. R7 lands first: smaller blast radius, and its
pump changes are untouched by R8 (which lives above the pump seam).

## S1 — Evidence gate: ndisrd error-code cross-check ✅ (2026-08-30)

- [x] Cross-checked against reachable evidence. The ndisrd driver is closed-source; Npcap
      (same NDIS filter-driver class, nmap/nmap#2036) confirms sleep-wake/adapter-removal
      surfaces as `ERROR_OPERATION_ABORTED` (995) and `STATUS_DEVICE_REMOVED`-projected codes.
      **No conflict** with the design table; `ERROR_GEN_FAILURE` (31) added to the transient
      set. Findings recorded in `design.md` §Error-classification. Degradation log records the
      full native error for future table refinement (ground truth from S8/production).

## S2 — R7 classifier + pump retry ✅ (2026-08-30)

- [x] `NdisNativeCallStatus.IsTransientReadError(int)` + unit tests (each classified code,
      unknown-code → permanent).
- [x] `NdisCapturePump.RunAsync` retry loop: classify caught `Win32Exception` (native code via
      `exception.NativeErrorCode`), bounded backoff (5 × 100ms-doubling, cap 1.6s), rate-limited
      warn `adapter.retry`, retry counters (internal telemetry properties).
- [x] Tests: fake `INdisPacketReader` — transient-then-success (run continues, counter
      incremented, handler still sees later packets); exhausted-transients → degraded exit;
      permanent → degraded exit immediately; cancellation during backoff unwinds.
      (`NdisCaptureResilienceTests.NdisCaptureResilienceTests` + `IsTransientReadError` cases.)
## S3 — R7 degradation plumbing ✅ (2026-08-30)

- [x] `NdisCapturePump`: optional `onDegraded(nativeError)` callback, invoked once at degraded
      exit (constructor parameter, mirrors `onBatchCompleted`).
- [x] `MultiAdapterCaptureLoop`: degraded pump completes without rethrow; sibling pumps NOT
      cancelled; forwards to new optional `onAdapterDegraded(adapter, nativeError)`; non-degraded
      exception path unchanged.
- [x] `TransactionalCaptureRuntime.MarkAdapterDegradedAsync(adapterId)`: gate-held `_applied`
      lookup + removal, best-effort `RestoreAsync` outside the lock, error log on restore failure
      (kept silent-best-effort per the class's existing posture; Program logs the incident).
      `RestoreBestEffortAsync` now snapshots `_applied` under the gate (first concurrent mutator).
- [x] Wire in `Program.RunCaptureLoopAsync` (error log + counters; keep top-level catch).
- [x] Tests: fake mode controller records `RestoreAsync` for the degraded adapter only; sibling
      pump runs to completion; second degradation of the same adapter is a no-op (snapshot
      already removed). (`CaptureDegradationPlumbingTests`.)
## S4 — R7 spec + telemetry wrap ✅ (2026-08-30)

- [x] Spec updates: `windows-ndisapi.md` failure table (retry/degrade rows), pump telemetry
      note; `error-handling.md` gains the R7 clause (process survives transient driver
      disturbances; single-adapter degradation semantics).
- [x] Full `dotnet test -c Release` green (496/496). **Review gate**: retry/degrade behavior
      summary presented in the implement report before R8 (same session, R8 below).
## S5 — R8 outcome + pending infrastructure ✅ (2026-08-30)

- [x] `TcpRedirectOutcome.SetupPending` enum member; audited all switch/usage sites (executor
      treats as silent consume with trace `packet.dropped reason=setupPending`; reverse/fragment
      handlers can never return it).
- [x] Pending index (`TcpPendingSynSetup`: retained materialized SYN copy + metadata + setup task
      slot, `TcpPendingSynSetupIndex`), own leaf lock; capacity 1024 entries; 1 MiB global
      Interlocked frame budget with exactly-once credit at every sink (completion, overwrite, TTL
      expiry, cap rejection); 5 s TTL (lazy expiry on touch + idle-sweep hook);
      overwrite-on-retransmit semantics.
- [x] Tests: cap rejection trace `tcp.setup.pending.dropped`; budget credit exactly-once under
      overwrite/expiry; TTL expiry. (`TcpPendingSynSetupTests`.)
## S6 — R8 fast path + background setup ✅ (2026-08-30)

- [x] `HandleSynAsync`: existing-association / tombstone / capacity fast paths synchronous and
      unchanged; new-flow branch → pending lookup/create/overwrite + `Task.Run` background setup;
      returns `SetupPending`; no awaits added to the pump-side new-flow path.
- [x] `SetupPendingAsync` (background): `EnterSetup`/`finally ExitSetup` wraps the body; listener
      `CreateAsync` → `TryClaim` (existing concurrent-loser path absorbs losers) → rewrite+inject
      the retained copy (the `SetupNewRedirectAsync` pipeline fed from the retained copy) →
      register + accept loop.
- [x] Genuine setup failure: drop pending entry, release listener, 1 s per-flow cooldown
      (pending-index cooldown dictionary, bounded evict-oldest), warn log; retransmitted SYN
      inside cooldown is silently dropped (`Dropped`).
- [x] Store-gate audit: no store-gate hold across bind; lock order pending-lock (leaf) never
      nested under store/table gates.
- [x] Tests: (a) slow fake factory + dispatch latency probe (pump not blocked); (b)
      retransmit-during-pending → exactly one listener (factory call count, gated-burst test);
      (c) exactly-one injected rewritten SYN; (d) loser releases redundant listener (branch kept
      as defense-in-depth; the pre-claim test shows the pump side re-injects instead — see
      report deviation note); (e) dispose during inflight background setup drains via the store's
      setup drain; (f) capacity/tombstone fast-path suites unchanged.
## S7 — R8 spec + full regression ✅ (2026-08-30)

- [x] Spec updates: `error-handling.md` TCP-side "never blocks the capture pump" clause;
      `windows-ndisapi.md` pump-ordering note (deferred SYN injection relaxes cross-flow
      ordering; per-flow ordering preserved by construction); `tcp-local-redirect.md` setup
      lifecycle section (R8 block).
- [x] Full `dotnet test -c Release` green (496/496); dispatcher warm-shape allocation gates
      untouched (no dispatcher changes; the proxy warm entry already routed through the executor
      and is allocation-identical).
## S8 — Optional VM spot-check ⏭ SKIPPED (2026-08-30)

- [ ] Skipped: no reachable Win11 VM in this environment (feasibility-dependent per the task
      dispatch). The R7 classification table records the full native error on every degraded exit
      precisely so a later hardware run (adapter disable/enable churn) can refine it; the VM
      validation remains open for the check phase / a hardware follow-up.
## Risky files / rollback points

- `src/WinForward.NdisApi/NdisCapture.cs` (pump loop — batching contract must not regress)
- `src/WinForward.Runtime/Capture/MultiAdapterCaptureLoop.cs`, `CaptureLifecycle.cs`
- `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs` (hot path; allocation discipline)
- `src/WinForward.Runtime/TcpRedirect/TcpRedirectSetup.cs` / `TcpRedirectSessionStore.cs`
  (D1 gate invariants)
- Rollback: S2–S3 and S5–S6 are each independently revertible (R7 and R8 touch disjoint files
  except none shared).
