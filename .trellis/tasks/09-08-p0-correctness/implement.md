# P0 implement — ordered execution plan

Validation commands (every batch): `dotnet build` (TreatWarningsAsErrors — must be warning-free)
and `dotnet test` (all green). Baseline totals recorded in batch 0 must be re-confirmed ±only the
batch's intentional additions.

## Batch 0 — Baseline capture `[required, once]`

- [ ] `dotnet build` → zero warnings (record).
- [ ] `dotnet test` → record total passed count + duration (expected ≈463 per spec; record actual).
- [ ] `git status` clean; create working branch if the session isn't on one already.

## Batch 1 — R2: IPv4Any constant (smallest, independent)

- [ ] `IPAddressValue.cs`: add `public static readonly IPAddressValue IPv4Any = new(0, AddressFamilyKind.IPv4);`
      near `FromIPv4` with a one-line doc comment.
- [ ] `PacketFlowClassifier.cs:40`: `Endpoint.From(IPAddressValue.IPv4Any, 0)`; remove `using System.Net;`.
- [ ] Validation: build + full `dotnet test` (totals unchanged);
      `rg -n "IPAddress\b|System\.Net" src/WinForward.Runtime/PacketFlowClassifier.cs` → empty.
- Commit: `fix(core): use IPAddressValue.IPv4Any on the non-flow classify path (hot-path contract 1)`.

## Batch 2 — R5: lease-null paramName, 9 sites

- [ ] Replace the guard-throw at FlowDispatcher.cs:128,163,254; TcpProxyCoordinator.cs:102,296,379,408,458;
      NdisPacketActionExecutor.cs:55 with `throw new ArgumentNullException("packet.Lease");`.
- [ ] `rg -n 'packet.Lease is null' src/` → 9 hits all followed by the new throw (no `nameof(packet)`).
- [ ] Add one test (most fitting existing executor test file): PassAsync on a lease-less packet
      throws `ArgumentNullException` with `ParamName == "packet.Lease"`.
- Commit: `fix(runtime): lease-null guards name the actual null member, not the struct`.

## Batch 3 — R3: ReadTable bounds validation

- [ ] `ProcessAttribution.cs`: `ReadTable` gains `out uint bytesWritten`; extract
      `internal static void ValidateRowCount(int rowCount, uint bytesWritten, int rowSize, string tableName)`
      throwing `InvalidOperationException` on `4 + (long)rowCount * rowSize > bytesWritten`.
- [ ] Wire validation into every `Read*` caller (`ReadTcp`, `ReadTcp6`, UDP variants) before row loops.
- [ ] Tests: pure `ValidateRowCount` cases — exact fit, 1-byte overflow, zero rows, minimum buffer.
- Commit: `fix(windows): cross-check iphlpapi table bounds before row deref (fail-closed)`.

## Batch 4 — R4: pump DisposeAsync awaits run exit

- [ ] `NdisCapture.cs`: completion source registered at `RunAsync` entry, completed in `finally`;
      `DisposeAsync` sets `_stopped`, awaits in-flight completion (none → fast path), releases
      buffers (idempotent). Update class + method docs with the new contract and pollDelay-bounded
      wait note.
- [ ] Tests (NdisCapturePumpTests, TCS-gated reader): dispose-mid-run completes only after loop
      exit; exactly-once buffer release; fast path after completed run.
- Commit: `fix(ndisapi): pump DisposeAsync waits for the run loop before freeing batch buffers`.

## Batch 5 — R1: lane retirement + overflow observability `[review gate after]`

- [ ] `NdisPacketActionExecutor`: add `RetireLanesExcept(ReadOnlySpan<nint>)` per design D1
      (creation lock, defensive drain with rate-limited warn, slot nulling, empty-span = retire all);
      add rate-limited Warn + `ImmediateSendLaneOverflowCount` in the AppendPass overflow branch;
      update the "never removed" comments (class doc + TryGetOrAddPendingLane doc) to the new
      between-generations contract.
- [ ] `DurableCaptureBundle`: add `OnScopeInstalled(IReadOnlyList<AdapterEnumerationItem>)`
      composing `UpdateUdpTargets(scope)` + `Executor.RetireLanesExcept(handles)`; Program.cs
      `onScopeInstalled` delegates to it.
- [ ] Executor tests: overflow counter + immediate send on lane 9; retire frees slots; empty span;
      defensive drain returns rented buffers exactly once.
- [ ] Refresh-churn regression: runner harness, ≥5 generations × 2 adapters (10 distinct handles);
      final generation batches (FakeReinjector batch call observed), overflow count 0.
- [ ] Validation: build + full `dotnet test` (totals = baseline + new tests); zero warnings.
- Commit: `fix(capture): retire pass lanes for out-of-scope adapters between generations`.

## Batch 6 — Wrap-up `[required, once]`

- [ ] Full-suite run: record final totals vs batch 0 baseline (+ list of added tests).
- [ ] rg-sweep verifications from design D2/D5 re-run and recorded.
- [ ] Optional (Windows host only): dispatcher warm-path benchmarks spot-check
      (`WarmPass*`/`WarmProxy*` allocation gates unchanged). Skip on Linux dev box; note it.
- [ ] Spec update (Phase 3.3): lane-lifecycle + pump-dispose contracts appended to
      `windows-ndisapi.md` (or `traffic-policy-lifecycle.md` if that's the better home);
      capture-composition note for `OnScopeInstalled`.
- [ ] Update this task's prd.md acceptance checkboxes; record totals.

## Rollback points

Every batch is one commit; `git revert <sha>` per batch. No batch depends on a later batch;
batch 5 is self-contained (API + wiring + tests together).

## Review gates

- After batch 5 (last code batch): run the full check flow (lint/build/test + rg sweeps) before
  the batch-6 wrap-up. Any failure → fix forward within the batch or revert the batch.
