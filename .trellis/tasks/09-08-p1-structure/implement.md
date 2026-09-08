# P1 implement — ordered execution plan

Validation per batch: `dotnet build` zero warnings + `dotnet test` all green, totals == 578
(doc-only spec edits don't count). Batch B2/B5 additionally build the benchmarks project.

## Batch 0 — Baseline `[once]`

- [ ] `dotnet build` + `dotnet test` → record 578/578, zero warnings.
- [ ] Branch `p1-structure` active (already created).

## Batch 1 — B1: Core re-homing (R3 / design D3)

- [ ] Extract `FlowTable` + `TransportTuple` from Domain.cs → new `src/WinForward.Core/FlowTable.cs`.
- [ ] Extract `BoundedSetupQueue` from PacketRuntime.cs → new `src/WinForward.Core/BoundedSetupQueue.cs`.
- [ ] Pure moves; ns `WinForward.Core` unchanged; effective-line check: both new files and both
      donors ≤400.
- Validation: build + test (578 unchanged). Commit: `refactor(core): re-home FlowTable and BoundedSetupQueue to their own files`.

## Batch 2 — B2: pump options record (R5 / design D5)

- [ ] Add `NdisCapturePumpOptions` record in NdisCapture.cs; ctor `(reader, handle, handler, options)`;
      validation/defaults verbatim; update MultiAdapterCaptureLoop + test call sites.
- Validation: build + test (578 unchanged); `dotnet build benchmarks/WinForward.Benchmarks`
      (captures any benchmark ctor usage). Commit: `refactor(ndisapi): collapse NdisCapturePump optional ctor parameters into an options record`.

## Batch 3 — B3: codec edge normalization (R4 / design D4)

- [ ] Verify Protocols/Socks5Udp.cs vs Runtime codec types; follow decision tree (dedupe or move);
      document residual edge in directory-structure.md sanctioned list + udp-relay.md cross-ref.
- Validation: build + test (578 unchanged); rg: no `using WinForward.Runtime.Socks5` remains in
      UdpProxy/ except the transport factory seam. Commit: `refactor(protocols): SOCKS5 datagram wire codec lives in Protocols; document the UdpProxy transport seam edge`.

## Batch 4 — B4: UdpProxyCoordinator split (R1 / design D1) `[review gate after]`

- [ ] Extract in order: A `UdpSetupCooldownTable` (leaf lock, tombstone→cooldown rename),
      B `UdpSetupQueueBudget` (Interlocked-only), C `UdpSessionSetup` (delegate-injected pipeline),
      D `UdpProxyLogging` (static mirror). Lock bodies verbatim; coordinator gate stays the only
      slot-state gate; five budget credit sinks keep single call sites.
- [ ] All new files in `src/WinForward.Runtime/UdpProxy/`; coordinator target ≈250 eff.
- [ ] Spec updates in same batch: tombstone→cooldown wording in tcp-local-redirect.md,
      udp-relay.md, quality-guidelines.md; split precedent in directory-structure.md.
- Validation: build + test (578 unchanged); effective lines: coordinator + 4 new files ≤400;
      rg `Tombstone` in UdpProxy/ → zero. Commit: `refactor(udp): split UdpProxyCoordinator into cooldown table, setup budget, session setup, and logging (tombstone naming retired for UDP)`.

## Batch 5 — B5: benchmarks reorganization (R2 / design D2)

- [ ] New `Stability/StabilityShared.cs` + `Stability/UdpBurstInstrumentation.cs`; UdpBurstScenario
      keeps orchestration; dedupe vs UdpLossScenario; BenchmarkShared → project root file + root ns;
      update Perf usings.
- Validation: `dotnet build benchmarks/WinForward.Benchmarks` zero warnings (tests untouched);
      effective lines all benchmark files ≤400 (UdpBurst ~150). Commit: `refactor(benchmarks): StabilityShared + burst instrumentation extraction; BenchmarkShared to project root`.

## Batch 6 — Wrap-up `[once]`

- [ ] Full build + test; totals 578 recorded; rg sweeps from design re-run.
- [ ] PRD acceptance checkboxes updated; spec updates verified present.
- [ ] trellis-check dispatch (full-scope), fix forward or revert per batch.

## Rollback points

Each batch one commit; `git revert <sha>` isolated. B4 internal order A→D is extraction-safety
ordered; if a later extraction fails validation, earlier extractions may land and the remainder
re-plans.

## Review gates

- After B4: full check flow before B5 (last-iteration full-scope check covers B1–B5 anyway).
