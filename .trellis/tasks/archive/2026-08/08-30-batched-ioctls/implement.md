# Implementation Plan — Batched reinjection IOCTLs (Phase 1)

Baseline: master @ 7393869, 463/463 tests, zero-warning build.

## Steps (ordered, each independently verifiable)

### S1 — Gate map concurrency (D4, independent win)
- [ ] `NdisAdapterGateMap`: `Lock + Dictionary` → `ConcurrentDictionary.GetOrAdd`;
      keep `GetMaxConcurrentCalls` behavior (snapshot via enumeration).
- [ ] Adjust/extend gate map unit tests if any assert lock internals.
- Validation: `dotnet build -c Release` zero warnings; full test suite green.

### S2 — ABI export verification (prereq for D1 resend logic)
- [ ] Inspect the real ndisapi.dll exports/headers for `SendPacketsToAdapter` /
      `SendPacketsToMstcp` `EthernetMultiRequest.PacketsSuccess` semantics (prefix
      count vs bitmask vs undefined). Record findings in task notes; if ambiguous,
      D1 takes the fail-the-batch fallback.
- Validation: written finding with evidence (dumpbin/header quote) in task notes.

### S3 — Driver batched send API (D1)
- [ ] `NdisApiDriver.SendPacketsToMstcp/ToAdapter(nint, NdisPacketBuffer[], int)`:
      BuildMultiRequest reuse (≤125 slots in the stackalloc budget; chunk above),
      one gate lease, clamp `PacketsSuccess`, suffix resend via existing single sends
      (or fail-batch per S2), rate-limited warn with both counts.
- [ ] Unit tests via the existing fake driver seam: all-success, partial-success
      suffix path, zero-success, oversized count chunking, gate lease single-acquire.
- Validation: new tests green; no API surface change elsewhere.

### S4 — Executor accumulator (D2)
- [ ] Per-(adapter, direction) fixed-size key list (≤4) in `NdisPacketActionExecutor`;
      `PassAsync` appends instead of sends; `FlushPendingPasses()` flushes in key
      insertion order, returns rented buffers exactly once, no-ops when empty.
- [ ] Debug-mode assert helper: pending count observed at flush (pins the
      every-iteration-flush invariant).
- [ ] Unit tests: same-key order preserved in flush; two keys flush independently;
      pooled-buffer exactly-once return; empty flush no-op; mixed materialized /
      in-place frames.
- Validation: new tests green; executor tests unchanged elsewhere (PassAsync external
  behavior identical when flush is called immediately — cover via an immediate-flush
  compatibility test).

### S5 — Batch-end wiring (D3)
- [ ] `NdisCapturePump`: optional `onBatchCompleted` callback after the slot loop
      (also from `finally` on loop exit). `MultiAdapterCaptureLoop` wires processor →
      executor flush.
- [ ] Call-site audit: every `PassAsync` caller is inside the pump batch loop chain
      (document the audit result in task notes).
- [ ] Tests: end-to-end capture-pump test with counting reinjector shows N packets →
      ≤2 reinjector calls per iteration, per-flow order preserved.
- Validation: full suite green; counting-reinjector assertion ≥10× call reduction
  under mixed pass load (AC5).

### S6 — Observability + docs
- [ ] Flush count / packets-per-flush histogram on the driver batched path (telemetry
      surface consistent with gate telemetry).
- [ ] Update `.trellis/spec/backend/windows-ndisapi.md`: batched send contract,
      ordering invariants, `PacketsSuccess` semantics, flush points.
- Validation: spec reviewer pass (self-review against existing spec format).

### S7 — Quality gate + commit
- [ ] Full test suite, zero-warning build, `CapturePumpBenchmarks` /
      `DispatcherBenchmarks` allocation gates not regressed (run before/after, same
      machine, in-process short job).
- [ ] trellis-check dispatch; commit.

## Rollback

- S1 is orthogonal (revert independently).
- S3–S5 land as one logical unit behind the executor flush; rollback = revert the
  wiring commit (S5) to disable batching while keeping driver API (S3) dormant.

## Review gates

- After S2: PacketsSuccess finding decides D1 branch (suffix resend vs fail-batch) —
  check with user if ambiguous.
- After S5: counting-reinjector numbers reported before S6.
