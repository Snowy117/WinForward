# Implement — Design-Deepening Refactors (R1–R12)

> Execution plan. Milestones are dependency-ordered and each ends in a revertable commit on master
> (repo precedent: 09-19-compat-api-cleanup). Behavior-neutral gates apply to every milestone:
> zero-warning `dotnet build -c Release`, targeted tests green mid-milestone, full suite equal to
> the M0 baseline at milestone boundaries, no assertion-semantics changes in test moves.

## Validation commands (repeat per milestone)

```bash
nix develop -c dotnet build -c Release                       # zero warnings required
nix develop -c dotnet test -c Release --filter "<filter>"    # targeted, mid-milestone
nix develop -c dotnet test -c Release                        # full, at milestone end == baseline
```

Filters per family:
- UDP: `UdpProxy|UdpSetupQueue|UdpRelay|UdpReceiveResilience|DurableCaptureBundle|HotPathAllocationGate`
- TCP: `TcpProxyCoordinator|TcpPendingSynSetup|TcpReversePrefilter|TcpFragmentHandling|FlowDispatcher`
- Setup: `SetupExecutor|TcpProxyCoordinator|UdpProxy|FlowDispatcher`
- Pump: `NdisCapture|CapturePump`
- Pre-final gates: `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario gc-soak --duration 20`

## M0 — Baseline + setup

- [ ] `trellis-before-dev` for the Runtime/Core/Cli layers (load spec context before any edit).
- [ ] Full build + full test run at HEAD; record the exact test count (expected 721 at `2ea413d`;
      re-record — the number in quality-guidelines.md (589) is stale and gets refreshed in Phase 3.3).
- [ ] Confirm git clean start: `git status`; workspace on master; no stray changes.
- No product-code edit in this milestone.

## M1 — SetupExecutor family (R1, R10, R12a)

Files: `SetupExecutor.cs`, `TcpProxyCoordinator.cs` (RentItem site), `UdpProxyCoordinator.cs`
(RentItem site), `DurableCaptureBundle.cs` (workerCount mapping line only), tests/benchmarks
`RentItem` users, `TcpPendingSynSetup.cs` (comment), `TcpPendingSynSetupTests.cs` (assertion test).

- [ ] Delete `SetupWorkKind` + `SetupWorkItem.Kind`; make `RentItem(Func<SetupWorkItem, Task>)`
      required-parameter; `Handler` non-nullable.
- [ ] Update all RentItem callers (lambda binding) — grep `RentItem` across src/tests/benchmarks.
- [ ] `SetupExecutor(int? workerCount = null, int ringCapacity = DefaultRingCapacity)`; bundle maps
      `SetupWorkerCount == 0 ? null : (int?)SetupWorkerCount` at the existing construction site.
- [ ] `Execute`: drop `!`; keep VSTHRD002 pragma + comment.
- [ ] R10: assertion test `DefaultRingCapacity >= TcpPendingSynSetupIndex.DefaultCapacity`;
      comment updated to inequality wording.
- [ ] R12a: `<remarks>` composition-only note on `ISetupExecutor`.
- [ ] Validate: Setup filter + full suite == baseline. Commit: `refactor(setup): require handler at rent time, drop dead work kind`.

## M2 — Core clock + hash seams (R8, R9)

Files: `FlowTable.cs`, `Domain.cs`, `UdpSetupQueueBudget.cs`, `UdpProxyCoordinator.cs` (1-line
budget construction site), `FlowTableTests.cs` (additive fake-clock test only).

- [ ] `FlowTable(capacity, TimeProvider?)`; `TryResolveLocked` uses `_timeProvider.GetUtcNow()`.
- [ ] `UdpSetupQueueBudget(long, IRuntimeLogger, TimeProvider)` required param; NoteDrop uses it.
- [ ] `FlowHash.Combine` in Domain.cs; both `GetHashCode` bodies delegate (values identical).
- [ ] Additive: FlowTable observation-refresh boundary test with fake TimeProvider.
- [ ] Validate: full suite == baseline (hash identity proved by existing resolve/collision tests).
      Commit: `refactor(core): inject clocks into FlowTable and UdpSetupQueueBudget; share FlowHash`.

## M3 — UDP family (R3, R4, R5, R6-UDP, R12b)

Files: `UdpProxyCoordinator.cs`, new `UdpProxyOptions`/`UdpProxyOptions.cs`,
`UdpSessionSetup.cs`, `UdpProxySession.cs` (+Context), new `IUdpSessionSlotHost.cs`, new
`UdpProxyDiagnostics.cs`, `UdpAdapterTargetSource.cs` (R12b remark), UDP tests + bundle UDP calls.

- [ ] R5 first (context record) — R4's fake host builds contexts.
- [ ] R4: `IUdpSessionSlotHost` + explicit implementation; UdpSessionSetup 12→8 params; one new
      direct test with fake host (proves the seam; mirror TcpRedirectSetup test style).
- [ ] R3: options record + single public ctor; internal ctor deleted; ~20 test sites mechanically
      rewritten; `BeforeExpiryRecheck`/`SetupQueueGlobalByteBudget` become internal init members.
- [ ] R6-UDP: `UdpProxyDiagnostics` snapshot; remove the 5 individual accessors; rewrite test paths.
- [ ] R12b: `<remarks>` on `IUdpAdapterTargetSource`.
- [ ] Validate: UDP filter + full suite. Commit: `refactor(udp): options record, slot-host seam, session context, diagnostics snapshot`.

## M4 — TCP family (R2, R6-TCP)

Files: `TcpProxyCoordinator.cs`, new `TcpRedirectOptions.cs`, new `TcpRedirectDiagnostics.cs`,
TCP tests + bundle TCP calls (options-object form).

- [ ] R2: `TcpRedirectOptions`; 11-param ctor → 6 required + options; 66 test sites mechanically
      rewritten (keep `ConcurrentLoserCount` assertions reachable via R6 path).
- [ ] R6-TCP: diagnostics snapshot (`HeldFlows` set + `HoldsFlow`); remove the 6 individual
      accessors; keep control surfaces.
- [ ] Validate: TCP filter + full suite; pay attention to the concurrent-SYN burst gate test
      (`ConcurrentLoserCount == N-1`).
      Commit: `refactor(tcp): options record and diagnostics snapshot`.

## M5 — NdisCapturePump (R7)

Files: `NdisCapture.cs` (options record + pump), new `NdisPumpDiagnostics` (same file or sibling per
line budget), pump tests + `CapturePumpBenchmarks`.

- [ ] `internal init` members `BatchCapacity`/`TransientRetryBaseDelay` on the options record
      (tests + benchmarks have IVT).
- [ ] `NdisPumpDiagnostics` snapshot; remove 4 accessors.
- [ ] Validate: pump filter + full suite. Commit: `refactor(ndis): pump diagnostics snapshot, test-seam options members`.

## M6 — Composition root (R11)

Files: `DurableCaptureBundle.cs`, new `TcpRedirectComposer.cs`, `UdpProxyComposer.cs`,
`BundlePools.cs` (or same-file cluster if small).

- [ ] Extract coordinator wiring into composers with composition records; move
      `PrimeSocks5AddressCacheAsync`; shared pool-name/RegisterPool helper.
- [ ] Bundle keeps: CreateAsync orchestration + rollback, BuildBundle, UpdateUdpTargets,
      OnScopeInstalled, DisposeCoreAsync ordering verbatim.
- [ ] Check bundle effective line count; if > 400, note child-split decision in the journal.
- [ ] Validate: full suite + lifeycle ordering tests + gc-soak smoke (20 s).
      Commit: `refactor(cli): extract coordinator composers from durable capture bundle`.

## M7 — Verification & close-out

- [ ] Full build zero warnings + full test count == M0 baseline.
- [ ] Allocation gates + gc-soak smoke re-run.
- [ ] Grep gates: `SetupWorkKind` → 0; `Handler!` → 0; `DateTimeOffset.UtcNow` in
      FlowTable/UdpSetupQueueBudget → 0.
- [ ] AC audit: walk prd.md AC1–AC3 + R1–R12; record per-item note location and evidence
      (design note = design.md section; evidence = commit + test run).
- [ ] Phase 3.3 spec updates:
  - quality-guidelines.md: refresh stale baseline count; update UdpProxyCoordinator signature line
    (`maximumFrameSize` → options); UdpSessionSetup "via ctor delegates" → `IUdpSessionSlotHost`
    seam; add options-record pattern + diagnostics snapshot convention.
  - directory-structure.md: add composer files to Cli layout; `IUdpSessionSlotHost` under UdpProxy.
- [ ] Phase 3.4 commits per repo convention (one per milestone, already committed; final spec commit).
- [ ] Archive task.

## Risky files / rollback points

- `TcpProxyCoordinator.cs` (66 test sites), `UdpProxyCoordinator.cs` (~20) — largest mechanical
  churn; revert granularity is the milestone commit.
- `DurableCaptureBundle.cs` — teardown ordering is load-bearing; only M6 touches it, and only
  wiring is moved, never reordered.
- No file deletions. New files: `UdpProxyOptions.cs`… (see M3/M4/M5/M6 lists), one new test file.

## Follow-up checks before `task.py start`

- [ ] prd.md converged (done), design.md + implement.md written (this file).
- [ ] implement.jsonl / check.jsonl curated with real entries (no `_example`).
- [ ] Final planning summary presented to the user; explicit approval received (separate message).
